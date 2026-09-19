namespace ZodiacBuddy.Stages.Animus;

internal partial class AnimusManager
{
    private void StartLeveRotationSolver()
    {
        _leveRotationSolver.Acquire(
            "[ZodiacBuddy/LEVE] Enabled RotationSolverReborn manual mode for leve combat.",
            "[ZodiacBuddy/LEVE] RSR manual-mode IPC was unavailable; command fallback used.");
    }

    private void StopLeveRotationSolver(string reason)
    {
        _leveRotationSolver.ReleaseNow(
            $"[ZodiacBuddy/LEVE] Disabled RotationSolverReborn because {reason}.",
            $"[ZodiacBuddy/LEVE] Used the RSR command fallback because {reason}.");
    }
}
