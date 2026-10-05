using System.Collections.Generic;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Bir ya da birkaç sunucu eyleminin işleyicisi (bkz. CommandDispatcher). Komut döngüsünde sırayla beklenir; uzun süren
    // iş (execute, update_agent, dosya aktarımı) kendi görevini başlatır.
    internal interface ICommandHandler
    {
        IReadOnlyList<string> Actions { get; }
        Task HandleAsync(ServerCommand command);
    }
}
