// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.Stores;
using BuildXL.Cache.ContentStore.Extensions;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Service.Grpc;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Utilities.Collections;
using ContentStore.Grpc;
#nullable enable
namespace BuildXL.Cache.ContentStore.Distributed.NuCache
{
    /// <summary>
    /// <see cref="CentralStorage"/> which uses uses distributed CAS as cache aside for a fallback central storage
    /// </summary>
    public class DistributedCentralStorage : CentralStorage, IDistributedContentCopierHost
    {
        private const string StorageIdSeparator = "||DCS||";
        private readonly DistributedCentralStoreConfiguration _configuration;
        private readonly ILocationStore _locationStore;
        private readonly DistributedContentCopier _copier;
        private const string CacheSubFolderName = "dcs";
        private const string CacheSubFolderNameWithTrailingSlash = CacheSubFolderName + @"\";

        private const string CacheSharedSubFolderToReplace = @"Shared\" + CacheSubFolderName;
        private const string CacheSharedSubFolder = CacheSubFolderName + @"\Shared";

        private readonly CentralStorage _fallbackStorage;
        private readonly ConcurrentDictionary<MachineLocation, MachineLocation> _machineLocationTranslationMap = new ConcurrentDictionary<MachineLocation, MachineLocation>();

        // Choosing MD5 hash type as hash type for peer to peer storage somewhat arbitrarily. However, it has the nice
        // property of not being a standard hash type used for normal CAS content so traffic can easily be differentiated
        private readonly HashType _hashType = HashType.MD5;

        // Randomly generated seed for use when computing derived hash represent fake content for tracking
        // which machines have started copying a particular piece of content
        private const uint _startedCopyHashSeed = 1006063109;
        private readonly FileSystemContentStoreInternal _privateCas;
        private readonly DisposableDirectory _copierWorkingDirectory;
        private int _translateLocationsOffset = 0;

        /// <inheritdoc />
        protected override Tracer Tracer { get; } = new Tracer(nameof(DistributedCentralStorage));

        /// <inheritdoc />
        protected override string PreprocessStorageId(string storageId) => storageId;

        /// <nodoc />
        public DistributedCentralStorage(
            DistributedCentralStoreConfiguration configuration,
            ILocationStore locationStore,
            DistributedContentCopier copier,
            CentralStorage fallbackStorage)
        {
            _configuration = configuration;
            _copier = copier;
            _fallbackStorage = fallbackStorage;
            _locationStore = locationStore;

            var maxRetentionMb = (int)Math.Ceiling(configuration.MaxRetentionGb * 1024);
            var softRetentionMb = (int)(maxRetentionMb * 0.8);

            var cacheFolder = configuration.CacheRoot / CacheSubFolderName;

            _copierWorkingDirectory = new DisposableDirectory(copier.FileSystem, cacheFolder / "Temp");

            // Create a private CAS for storing checkpoint data
            // Avoid introducing churn into primary CAS
            _privateCas = new FileSystemContentStoreInternal(
                copier.FileSystem,
                SystemClock.Instance,
                cacheFolder,
                new ConfigurationModel(
                    new ContentStoreConfiguration(new MaxSizeQuota(hardExpression: maxRetentionMb + "MB", softExpression: softRetentionMb + "MB")),
                    ConfigurationSelection.RequireAndUseInProcessConfiguration),
                settings: new ContentStoreSettings()
                {
                    TraceFileSystemContentStoreDiagnosticMessages = _configuration.TraceFileSystemContentStoreDiagnosticMessages,
                    SelfCheckSettings = _configuration.SelfCheckSettings,
                });
        }

        #region IDistributedContentCopierHost Members

        AbsolutePath IDistributedContentCopierHost.WorkingFolder => _copierWorkingDirectory.Path;

        void IDistributedContentCopierHost.ReportReputation(MachineLocation location, MachineReputation reputation)
        {
            // Don't report reputation as this component modifies machine locations so they won't be recognized
            // by the machine reputation tracker
        }

        #endregion IDistributedContentCopierHost Members

        /// <inheritdoc />
        protected override async Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            await _privateCas.StartupAsync(context).ThrowIfFailure();

            return await base.StartupCoreAsync(context);
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            await _privateCas.ShutdownAsync(context).ThrowIfFailure();

            _copierWorkingDirectory.Dispose();

            return await base.ShutdownCoreAsync(context);
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> TouchBlobCoreAsync(OperationContext context, AbsolutePath file, string storageId, bool isUploader, bool isImmutable)
        {
            var (hash, fallbackStorageId) = ParseCompositeStorageId(storageId);

            // Need to touch in fallback storage as well so it knows the content is still in use
            var touchTask = _fallbackStorage.TouchBlobAsync(context, file, fallbackStorageId, isUploader, isImmutable).ThrowIfFailure();

            // Ensure content is present in private CAS and registered
            var registerTask = PutAndRegisterFileAsync(context, file, hash, isImmutable);

            await Task.WhenAll(touchTask, registerTask);

            return BoolResult.Success;
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> TryGetFileCoreAsync(OperationContext context, string storageId, AbsolutePath targetFilePath, bool isImmutable)
        {
            // Get the content from peers or fallback
            var contentHashWithSize = await TryGetAndPutFileAsync(context, storageId, targetFilePath, isImmutable).ThrowIfFailureAsync();

            // Register that the machine now has the content
            await RegisterContent(context, contentHashWithSize);

            return BoolResult.Success;
        }

        /// <inheritdoc />
        protected override async Task<Result<string>> UploadFileCoreAsync(OperationContext context, AbsolutePath file, string blobName, bool garbageCollect = false)
        {
            // Add the file to CAS and register with global content location store.
            var putResult = await PutAndRegisterFileAsync(context, file, hash: null);

            if (putResult.Succeeded && _configuration.ProactiveCopyCheckpointFiles)
            {
                var hashWithSize = new ContentHashWithSize(putResult.ContentHash, putResult.ContentSize);
                var pushResult = await PushCheckpointFileAsync(context, hashWithSize)
                    .FireAndForgetOrInlineAsync(context, _configuration.InlineCheckpointProactiveCopies);

                if (!pushResult.Succeeded)
                {
                    return new Result<string>(pushResult);
                }
            }

            string fallbackStorageId = await _fallbackStorage.UploadFileAsync(
                context,
                file,
                name: $"{blobName}.{putResult.ContentHash.Serialize(delimiter: '.')}",
                garbageCollect).ThrowIfFailureAsync();

            return CreateCompositeStorageId(putResult.ContentHash, fallbackStorageId);
        }

        /// <summary>
        /// TODO: try to refactor this to use the same logic as ReadOnlyDistributedContentSession.
        /// </summary>
        private Task<PushFileResult> PushCheckpointFileAsync(OperationContext context, ContentHashWithSize hashWithSize)
        {
            return context.PerformOperationAsync(Tracer, async () =>
            {
                var destinationMachineResult = _locationStore.ClusterState.GetRandomMachineLocation(Array.Empty<MachineLocation>());
                if (!destinationMachineResult.Succeeded)
                {
                    return new PushFileResult(destinationMachineResult, "Failed to get a location to proactively copy the checkpoint file.");
                }

                var destionationMachine = destinationMachineResult.Value;

                var streamResult = await _privateCas.OpenStreamAsync(context, hashWithSize.Hash, pinRequest: null);
                if (!streamResult.Succeeded)
                {
                    return new PushFileResult(streamResult, "Should have been able to open the stream from the local CAS");
                }

                using var stream = streamResult.Stream!;
                return await _copier.PushFileAsync(
                    context,
                    hashWithSize,
                    destionationMachine,
                    stream,
                    isInsideRing: false,
                    CopyReason.ProactiveCheckpointCopy,
                    ProactiveCopyLocationSource.Random,
                    attempt: 0);
            },
            extraStartMessage: $"Hash=[{hashWithSize.Hash.ToShortString()}]",
            extraEndMessage: _ => $"Hash=[{hashWithSize.Hash.ToShortString()}]");
        }

        private async Task<Result<ContentHashWithSize>> TryGetAndPutFileAsync(OperationContext context, string storageId, AbsolutePath targetFilePath, bool isImmutable)
        {
            var (hash, fallbackStorageId) = ParseCompositeStorageId(storageId);
            if (hash != null)
            {
                var fileAccessMode = isImmutable ? FileAccessMode.ReadOnly : FileAccessMode.Write;
                var fileRealizationMode = isImmutable ? FileRealizationMode.Any : FileRealizationMode.Copy;

                // First attempt to place file from content store
                var placeResult = await _privateCas.PlaceFileAsync(context, hash.Value, targetFilePath, fileAccessMode, FileReplacementMode.ReplaceExisting, fileRealizationMode, pinRequest: null);
                if (placeResult.IsPlaced())
                {
                    return Result.Success(new ContentHashWithSize(hash.Value, placeResult.FileSize));
                }

                // If not placed, try to copy from a peer into private CAS
                var putResult = await CopyLocalAndPutAsync(context, hash.Value);
                if (putResult.Succeeded)
                {
                    // Lastly, try to place again now that file is copied to CAS
                    placeResult = await _privateCas.PlaceFileAsync(context, hash.Value, targetFilePath, fileAccessMode, FileReplacementMode.ReplaceExisting, fileRealizationMode, pinRequest: null).ThrowIfFailure();

                    Counters[CentralStorageCounters.TryGetFileFromPeerSucceeded].Increment();
                    return Result.Success(new ContentHashWithSize(hash.Value, placeResult.FileSize));
                }
                else
                {
                    Tracer.Debug(context, $"Falling back to blob storage. Error={putResult}");
                }
            }

            Counters[CentralStorageCounters.TryGetFileFromFallback].Increment();
            return await TryGetFromFallbackAndPutAsync(context, targetFilePath, fallbackStorageId, isImmutable);
        }

        private string CreateCompositeStorageId(ContentHash hash, string fallbackStorageId)
        {
            // Storage id format:
            // {Hash}{StorageIdSeparator}{fallbackStorageId}
            return string.Join(StorageIdSeparator, hash, fallbackStorageId);
        }

        internal static (ContentHash? hash, string fallbackStorageId) ParseCompositeStorageId(string storageId)
        {
            if (storageId.Contains(StorageIdSeparator))
            {
                // Storage id is a composite id. Split out parts
                var parts = storageId.Split(new[] { StorageIdSeparator }, StringSplitOptions.None);
                Contract.Assert(parts.Length == 2);
                return (hash: new ContentHash(parts[0]), fallbackStorageId: parts[1]);
            }
            else
            {
                // Storage id is not a composite id. This happens when we get ids from when the distributed central
                // storage is disabled. Just return the full storage id as the fallback storage id
                return (hash: null, fallbackStorageId: storageId);
            }
        }

        private Task<PutResult> CopyLocalAndPutAsync(OperationContext operationContext, ContentHash hash)
        {
            return operationContext.PerformOperationWithTimeoutAsync(
                Tracer,
                async context =>
                {
                    var startedCopyHash = ComputeStartedCopyHash(hash);
                    await RegisterContent(context, new ContentHashWithSize(startedCopyHash, -1));

                    for (int i = 0; i < _configuration.PropagationIterations; i++)
                    {
                        // If initial place fails, try to copy the content from remote locations
                        var (hashInfo, pendingCopyCount) = await GetFileLocationsAsync(context, hash, startedCopyHash);

                        var machineId = _locationStore.ClusterState.PrimaryMachineId.Index;
                        int machineNumber = GetMachineNumber();
                        var requiredReplicas = ComputeRequiredReplicas(machineNumber);

                        var actualReplicas = hashInfo.Locations?.Count ?? 0;

                        // Copy from peers if:
                        // The number of pending copies is known to be less that the max allowed copies
                        // OR the number replicas exceeds the number of required replicas computed based on the machine index
                        bool shouldCopy = pendingCopyCount < _configuration.MaxSimultaneousCopies || actualReplicas >= requiredReplicas;

                        Tracer.Debug(context, $"{i} (ShouldCopy={shouldCopy}): Hash={hash.ToShortString()}, Id={machineId}" +
                            $", Replicas={actualReplicas}, RequiredReplicas={requiredReplicas}, Pending={pendingCopyCount}, Max={_configuration.MaxSimultaneousCopies}");

                        if (shouldCopy)
                        {
                            return await _copier.TryCopyAndPutAsync(
                                context,
                                new DistributedContentCopier.CopyRequest(
                                    this,
                                    hashInfo,
                                    CopyReason.CentralStorage,
                                    args => _privateCas.PutFileAsync(context, args.tempLocation, FileRealizationMode.Move, hash, pinRequest: null),
                                    // Most of these transfers are large files (sst files), but they are also already
                                    // compressed, so compressing over it would only waste cycles.
                                    CopyCompression.None
                                    ));
                        }

                        // Wait for content to propagate to more machines
                        await Task.Delay(_configuration.PropagationDelay, context.Token);
                    }

                    return new PutResult(hash, "Insufficient replicas");
                },
                traceErrorsOnly: true,
                extraEndMessage: _ => $"ContentHash=[{hash}]",
                timeout: _configuration.PeerToPeerCopyTimeout);
        }

        /// <summary>
        /// Try to get from the fallback and put in the CAS
        /// </summary>
        private async Task<ContentHashWithSize> TryGetFromFallbackAndPutAsync(OperationContext context, AbsolutePath targetFilePath, string fallbackStorageId, bool isImmutable)
        {
            // In the success case the content will be put at targetFilePath
            await _fallbackStorage.TryGetFileAsync(context, fallbackStorageId, targetFilePath, isImmutable).ThrowIfFailure();

            var placementFileRealizationMode = isImmutable ? FileRealizationMode.Any : FileRealizationMode.Copy;
            var putResult = await _privateCas.PutFileAsync(context, targetFilePath, placementFileRealizationMode, _hashType, pinRequest: null).ThrowIfFailure();

            return new ContentHashWithSize(putResult.ContentHash, putResult.ContentSize);
        }

        private async Task<PutResult> PutAndRegisterFileAsync(OperationContext context, AbsolutePath file, ContentHash? hash, bool isImmutable = false)
        {
            var putFileRealizationMode = isImmutable ? FileRealizationMode.Any : FileRealizationMode.Copy;

            PutResult putResult;
            if (hash != null)
            {
                putResult = await _privateCas.PutFileAsync(context, file, putFileRealizationMode, hash.Value, pinRequest: null).ThrowIfFailure();
            }
            else
            {
                putResult = await _privateCas.PutFileAsync(context, file, putFileRealizationMode, _hashType, pinRequest: null).ThrowIfFailure();
            }

            var contentInfo = new ContentHashWithSize(putResult.ContentHash, putResult.ContentSize);
            await RegisterContent(context, contentInfo);

            return putResult;
        }

        private async Task<(ContentHashWithSizeAndLocations info, int pendingCopies)> GetFileLocationsAsync(OperationContext context, ContentHash hash, ContentHash startedCopyHash)
        {
            // Locations are registered under the derived fake startedCopyHash to keep a count of which machines have started
            // copying content. This allows computing the amount of pending copies by subtracting the machines which have
            // finished copying (i.e. location is registered with real hash)
            var result = await _locationStore.GetBulkAsync(context, new[] { hash, startedCopyHash }).ThrowIfFailure();
            var info = result.ContentHashesInfo[0];

            var startedCopyLocations = result.ContentHashesInfo[1].Locations!;
            var finishedCopyLocations = info.Locations!;
            var pendingCopies = startedCopyLocations.Except(finishedCopyLocations).Count();

            return (new ContentHashWithSizeAndLocations(info.ContentHash, info.Size, TranslateLocations(info.Locations!)), pendingCopies);
        }

        private ContentHash ComputeStartedCopyHash(ContentHash hash)
        {
            var murmurHash = MurmurHash3.Create(hash.ToByteArray(), _startedCopyHashSeed);

            var hashLength = HashInfoLookup.Find(_hashType).ByteLength;
            var buffer = murmurHash.ToByteArray();
            Array.Resize(ref buffer, hashLength);

            return new ContentHash(_hashType, buffer);
        }

        private Task RegisterContent(OperationContext context, params ContentHashWithSize[] contentInfo)
        {
            return _locationStore.RegisterLocalLocationAsync(context, contentInfo).ThrowIfFailure();
        }

        private IReadOnlyList<MachineLocation> TranslateLocations(IReadOnlyList<MachineLocation> locations)
        {
            // Choose a 'random' offset to ensure that locations are random
            // Locations are normally randomly sorted except machine reputation can override this
            // For content which is pulled on all machines like that in the central storage, it is more
            // important not to overload a machine which may end up consistent at the top of the list because of
            // having a good reputation
            var offset = Interlocked.Increment(ref _translateLocationsOffset);
            return locations.SelectList((item, index) => TranslateLocation(locations[(offset + index) % locations.Count]));
        }

        private MachineLocation TranslateLocation(MachineLocation other)
        {
            if (_machineLocationTranslationMap.TryGetValue(other, out var translated))
            {
                return translated;
            }

            var otherPath = other.Path;

            bool hasTrailingSlash = otherPath.EndsWith(@"\");

            // Add dcs subfolder to the path
            otherPath = Path.Combine(otherPath, hasTrailingSlash ? CacheSubFolderNameWithTrailingSlash : CacheSubFolderName);

            // If other already ended with shared, this will rearrange so that the shared folder is under the dcs sub folder
            otherPath = otherPath.ReplaceIgnoreCase(CacheSharedSubFolderToReplace, CacheSharedSubFolder);

            var location = new MachineLocation(otherPath);
            _machineLocationTranslationMap[other] = location;
            return location;
        }

        /// <summary>
        /// Computes an index for the machine among active machines
        /// </summary>
        private int GetMachineNumber()
        {
            var machineId = _locationStore.ClusterState.PrimaryMachineId.Index;
            var machineNumber = machineId - _locationStore.ClusterState.InactiveMachines.Where(id => id.Index < machineId).Count();
            return machineNumber;
        }

        private int ComputeRequiredReplicas(int index)
        {
            if (index <= 0)
            {
                return 1;
            }

            // Threshold is index / MaxSimultaneousCopies.
            // This ensures when locations are chosen at random there should be on average MaxSimultaneousCopies or less
            // from the set of locations assuming worst case where all machines are trying to copy concurrently
            var machineThreshold = index / _configuration.MaxSimultaneousCopies;
            return Math.Max(1, machineThreshold);
        }

        /// <summary>
        /// Opens stream to content in inner content store
        /// </summary>
        public Task<OpenStreamResult> StreamContentAsync(Context context, ContentHash contentHash)
        {
            return _privateCas.OpenStreamAsync(context, contentHash, pinRequest: null);
        }

        /// <summary>
        /// Checks whether the inner content store has the content
        /// </summary>
        public bool HasContent(ContentHash contentHash)
        {
            return _privateCas.Contains(contentHash);
        }

        /// <summary>
        /// Defines content location store functionality needed for <see cref="DistributedCentralStorage"/>
        /// </summary>
        public interface ILocationStore
        {
            /// <summary>
            /// The cluster state
            /// </summary>
            ClusterState ClusterState { get; }

            /// <summary>
            /// Gets content locations for content
            /// </summary>
            Task<GetBulkLocationsResult> GetBulkAsync(OperationContext context, IReadOnlyList<ContentHash> contentHashes);

            /// <summary>
            /// Registers content location for current machine
            /// </summary>
            Task<BoolResult> RegisterLocalLocationAsync(OperationContext context, IReadOnlyList<ContentHashWithSize> contentInfo);
        }
    }
}
