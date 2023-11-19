using System.Collections.Concurrent;
using Windows.Storage;

namespace RightProperties;

static class Property {
    static private ConcurrentBag<IDictionary<string, object>> propsDict = new ConcurrentBag<IDictionary<string, object>>();

    static private bool probeMissingVideoProps = true;

    static private bool recursiveTraversal = true;

    static private async Task TraversalFolderItemsProps(string folderPath) {
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(folderPath).AsTask(Const.CTS.Token).ConfigureAwait(false);
        IReadOnlyList<IStorageItem> items = await folder.GetItemsAsync().AsTask(Const.CTS.Token).ConfigureAwait(false);
        var tasks = new List<Task>();

        foreach (IStorageItem item in items) {
            Const.CTS.Token.ThrowIfCancellationRequested();
            if (item is StorageFile file)
                tasks.Add(Task.Run(async () => {
                    try {
                        IDictionary<string, object> prop = await file.Properties.RetrievePropertiesAsync(null).AsTask(Const.CTS.Token).ConfigureAwait(false);
                        if (probeMissingVideoProps) {
                            if (file.ContentType.StartsWith("video") || (int)prop["System.PerceivedType"] == 4) {
                                (string entriesArg, HashSet<int> queryIndexes) = FFProbe.PrepareFFProbeEntriesArgument(prop);
                                if (entriesArg.Length > 0) prop = await FFProbe.GetMetadataFromFFProbe(file, entriesArg, queryIndexes, prop).ConfigureAwait(false);
                            } else Logger.Info("Skip trying to retrieve props via ffprobe from non-video file `{0}`({1}).", file.Path, file.ContentType);
                        }

                        // It's `WinRT.IInspectable` which can't be serialized by `JsonSerializer`(System.NotSupportedException: Serialization and deserialization of 'System.IntPtr').
                        // TODO: Get thumbnail.
                        prop.Remove("System.ThumbnailStream");
                        propsDict.Add(prop);
                    } catch {
                        if (!Const.CTS.IsCancellationRequested) {
                            Const.CTS.Cancel();
                            Const.CTS.Dispose();
                        }

                        throw;
                    }
                }, Const.CTS.Token));
            else if (recursiveTraversal && item.IsOfType(StorageItemTypes.Folder))
                tasks.Add(TraversalFolderItemsProps(item.Path));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    static public void DisableProbeMissingVideoProps() {
        probeMissingVideoProps = false;
    }

    static public void DisableRecursiveTraversal() {
        recursiveTraversal = false;
    }

    static public void TryCheckFFProbeBinary() {
        if (!probeMissingVideoProps) return;
        FFProbe.CheckFFProbeBinary();
    }

    /// <summary>
    /// Retrieve all items' properties of given folder, recursively.
    /// If any video file's Windows Properties listed on <see cref="FFProbe.metaEntries" /> are missing,
    /// it uses ffprobe to retrieve the missing one.
    /// </summary>
    static public async Task<ConcurrentBag<IDictionary<string, object>>> CollectItemsPropsOfFolder(string folderPath) {
        try {
            Const.WATCHER.Restart();
            await TraversalFolderItemsProps(folderPath).ConfigureAwait(false);
            Const.WATCHER.Stop();

            Logger.Info("Total files: {0}", propsDict.Count);
            Logger.Info("Took {0} seconds to retrieve all files' props.", Const.WATCHER.Elapsed.TotalSeconds);
        } catch (Exception e) {
            Logger.Error(e.ToString());
            System.Environment.Exit(1);
        }

        return propsDict;
    }

    /// <summary>
    /// Retrieve properties of given VIDEO file.
    /// If file's Windows Properties listed on <see cref="FFProbe.metaEntries" /> are missing,
    /// it uses ffprobe to retrieve the missing one.
    /// </summary>
    /// <remarks>
    /// ONLY FOR DEBUG.
    /// </remarks>
    static public async Task QueryWithFFProbeAndExit(string path) {
        StorageFile f = await StorageFile.GetFileFromPathAsync(path).AsTask(Const.CTS.Token).ConfigureAwait(false);
        IDictionary<string, object> p = await f.Properties.RetrievePropertiesAsync(null).AsTask(Const.CTS.Token).ConfigureAwait(false);
        (string ea, HashSet<int> qi) = FFProbe.PrepareFFProbeEntriesArgument(p);

        Const.WATCHER.Restart();
        if (ea.Length > 0) p = await FFProbe.GetMetadataFromFFProbe(f, ea, qi, p).ConfigureAwait(false);
        Const.WATCHER.Stop();

        Logger.WriteColorText(ConsoleColor.Green, "Took {0} seconds to retrieve all props.", Const.WATCHER.Elapsed.TotalSeconds);
        Logger.WriteColorText(ConsoleColor.Blue, "{0}", System.Text.Json.JsonSerializer.Serialize(p, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        System.Environment.Exit(0);
    }
}