namespace RightProperties;

static class Cli {
    static private string lookUpPath = "";

    static public string GetLookUpPath() {
        return lookUpPath;
    }

    static public void ParseArgs(string[] args) {
        for (int index = 0; index < args.Length; index++) {
            var arg = args[index];
            if (arg.StartsWith('-')) {
                if (arg == "--log-level") {
                    string[] levelOpts = new[] { "debug", "info", "warn", "error", "silent" };
                    string level = args[index + 1];
                    int levelIdx = Array.IndexOf(levelOpts, level);
                    if (levelIdx == -1) throw new Exception($"`--log-level` expects {String.Join(", ", levelOpts)}, but got `{level}`.");
                    Logger.SetLogLevel((LogLevel)levelIdx);
                    index++;
                } else if (arg == "--ffprobe-bin") {
                    FFProbe.SetBinaryPath(Util.GetAbsolutePath(args[index + 1]));
                    index++;
                } else if (arg == "--no-video-missing-props-probe") Property.DisableProbeMissingVideoProps();
                else if (arg == "--no-recursive") Property.DisableRecursiveTraversal();
                else throw new Exception($"Unknown option `{arg}`.");
            } else {
                // Positional parameter
                if (lookUpPath != "") throw new Exception($"You must provide only one lookup folder.");
                lookUpPath = Util.GetAbsolutePath(arg);
                // Although `StorageFolder.GetFolderFromPathAsync` will throw if target isn't a folder.
                Util.CheckIfPathIsFolder(lookUpPath);
            }
        }
    }
}