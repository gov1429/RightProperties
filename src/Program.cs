using System.Diagnostics.CodeAnalysis;
using RightProperties;

// Json
// https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/how-to?pivots=dotnet-7-0
// https://stackoverflow.com/q/58003293

// Async/Await
// https://stackoverflow.com/a/37647093

// Display all kinds of characters.
Console.OutputEncoding = System.Text.Encoding.UTF8;
// Console.WriteLine(Console.OutputEncoding.CodePage);

Console.CancelKeyPress += (_, e) => {
    Logger.Info("CancelKeyPress fired");
    Const.CTS.Cancel();
    Const.CTS.Dispose();
    e.Cancel = true;
};

Cli.ParseArgs(args);
Property.TryCheckFFProbeBinary();

var propsDict = await Property.CollectItemsPropsOfFolder(Cli.GetLookUpPath()).ConfigureAwait(false);
Util.WriteJsonFile($"props.{DateTime.Now.ToString("o").Replace(':', '_')}.json", propsDict);
