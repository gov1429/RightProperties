namespace RightProperties;

// // Review all colors.
// foreach (var value in Enum.GetValues(typeof(ConsoleColor)))
//     Logger.WriteColorText((ConsoleColor)value, "This is `{0}`.", value);

enum LogLevel {
    Debug,
    Info,
    Warn,
    Error,
    Silent
}

static class Logger {
    static private LogLevel level = LogLevel.Info;

    static private Dictionary<LogLevel, ConsoleColor> levelColorMap = new Dictionary<LogLevel, ConsoleColor> {
        { LogLevel.Debug, ConsoleColor.Green },
        { LogLevel.Info, ConsoleColor.Cyan },
        { LogLevel.Warn, ConsoleColor.Yellow },
        { LogLevel.Error, ConsoleColor.Red },
    };

    static public void SetLogLevel(LogLevel logLevel) {
        level = logLevel;
    }

    static public void WriteColorText(ConsoleColor color, string format, params object[] objs) {
        // By setting `ForegroundColor` which is not thread safety, so we use ansi escape code.
        // See: https://github.com/dotnet/runtime/tree/2c62994efb2495dcaef2312de3ab25ea4792b23a/src/libraries/Microsoft.Extensions.Logging.Console
        // See: https://github.com/edokan/Edokan.KaiZen.Colors/tree/68545bb9a86502a196f27cf636d15a9b6399d7d4/Edokan.KaiZen.Colors

        int escapeCode = color switch {
            ConsoleColor.Black or ConsoleColor.DarkGray => 30,
            ConsoleColor.DarkRed => 31,
            ConsoleColor.DarkGreen => 32,
            ConsoleColor.DarkYellow => 33,
            ConsoleColor.DarkBlue => 34,
            ConsoleColor.DarkMagenta => 35,
            ConsoleColor.DarkCyan => 36,
            ConsoleColor.Gray => 37,
            ConsoleColor.Red => 91,
            ConsoleColor.Green => 92,
            ConsoleColor.Yellow => 93,
            ConsoleColor.Blue => 94,
            ConsoleColor.Magenta => 95,
            ConsoleColor.Cyan => 96,
            ConsoleColor.White => 97,
            _ => 39
        };

        Console.WriteLine($"\x1b[{escapeCode}m{format}\x1b[39m", objs);
    }

    static public void Log(LogLevel logLevel, string format, params object[] objs) {
        if (level > logLevel || level == LogLevel.Silent || logLevel == LogLevel.Silent) return;
        WriteColorText(levelColorMap[logLevel], format, objs);
    }

    static public void Debug(string format, params object[] objs) {
        Log(LogLevel.Debug, format, objs);
    }

    static public void Info(string format, params object[] objs) {
        Log(LogLevel.Info, format, objs);
    }

    static public void Warn(string format, params object[] objs) {
        Log(LogLevel.Warn, format, objs);
    }

    static public void Error(string format, params object[] objs) {
        Log(LogLevel.Error, format, objs);
    }

    static public void Silent(string format, params object[] objs) {
    }
}