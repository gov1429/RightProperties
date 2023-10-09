using System.Diagnostics.CodeAnalysis;

namespace RightProperties;

static class Util {
    static public string GetAbsolutePath(string path) {
        return Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path);
    }

    static public void CheckIfPathIsFolder(string path) {
        if (!File.GetAttributes(path).HasFlag(System.IO.FileAttributes.Directory))
            throw new Exception($"The path you provided, `{path}`, isn't a folder.");
    }

    static public void WriteJsonFile<TValue>(string filePath, TValue value) {
        // // TODO: The application called an interface that was marshalled for a different thread. https://stackoverflow.com/a/25286313
        // using FileStream createStream = File.Create(filePath);
        // await System.Text.Json.JsonSerializer.SerializeAsync(createStream, value, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).ConfigureAwait(false);
        // TODO: Can we "serialize" in parallel?
        Const.WATCHER.Restart();
        File.WriteAllText(filePath, System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Const.WATCHER.Stop();

        Logger.Info("Took {0} seconds to write json.", Const.WATCHER.Elapsed.TotalSeconds);
    }
}
