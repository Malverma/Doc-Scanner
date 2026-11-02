using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.TwainWorker;

/// <summary>Sends JSON events to the app over stdout. The app logs them; receipts never pass through here.</summary>
internal static class Output
{
    public static void Emit(WorkerEvent workerEvent)
    {
        Console.Out.WriteLine(WorkerProtocol.Serialize(workerEvent));
        Console.Out.Flush();
    }

    public static void Log(string message) => Emit(new WorkerEvent { Type = WorkerEvent.Log, Message = message });
}
