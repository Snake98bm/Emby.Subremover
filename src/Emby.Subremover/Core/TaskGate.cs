using System.Threading;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Emby can run different scheduled tasks at the same time. Removing and restoring both change videos and the
    /// record of backups, so only one of them may run at once.
    /// </summary>
    public static class TaskGate
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        public static bool TryEnter() => Gate.Wait(0);

        public static void Exit() => Gate.Release();
    }
}
