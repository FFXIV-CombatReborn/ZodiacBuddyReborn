using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using ECommons.DalamudServices;
using ECommons.Logging;
using ECommons.MathHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ZodiacBuddy.Stages.Atma.Unstuck;

namespace ZodiacBuddy.Stages.Atma.Movement;

[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public unsafe struct PlayerMoveControllerFlyInput
{
    [FieldOffset(0x0)] public float Forward;
    [FieldOffset(0x4)] public float Left;
    [FieldOffset(0x8)] public float Up;
    [FieldOffset(0xC)] public float Turn;
    [FieldOffset(0x10)] public float u10;
    [FieldOffset(0x14)] public byte DirMode;
    [FieldOffset(0x15)] public byte HaveBackwardOrStrafe;
}

[StructLayout(LayoutKind.Explicit, Size = 0x2B0)]
public unsafe struct CameraEx
{
    [FieldOffset(0x130)] public float DirH; // 0 is north, increases CW
    [FieldOffset(0x134)] public float DirV; // 0 is horizontal, positive is looking up, negative looking down
    [FieldOffset(0x138)] public float InputDeltaHAdjusted;
    [FieldOffset(0x13C)] public float InputDeltaVAdjusted;
    [FieldOffset(0x140)] public float InputDeltaH;
    [FieldOffset(0x144)] public float InputDeltaV;
    [FieldOffset(0x148)] public float DirVMin; // -85deg by default
    [FieldOffset(0x14C)] public float DirVMax; // +45deg by default
}

public unsafe class OverrideMovement : IDisposable
{
    public bool Enabled
    {
        get => _rmiWalkHook.IsEnabled;
        set
        {
            if (value)
            {
                _rmiWalkHook.Enable();
                _rmiFlyHook.Enable();
            }
            else
            {
                _rmiWalkHook.Disable();
                _rmiFlyHook.Disable();
            }
        }
    }
    public AdvancedUnstuck? AdvancedUnstuck { get; set; }
    public bool IgnoreUserInput; // if true - override even if user tries to change camera orientation, otherwise override only if user does nothing
    public Vector3 DesiredPosition;
    public float Precision = 0.01f;

    private bool _legacyMode;

    private delegate void RMIWalkDelegate(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk);
    [Signature("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D")]
    private Hook<RMIWalkDelegate> _rmiWalkHook = null!;

    private delegate void RMIFlyDelegate(void* self, PlayerMoveControllerFlyInput* result);
    [Signature("E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8")]
    private Hook<RMIFlyDelegate> _rmiFlyHook = null!;

    public OverrideMovement()
    {
        Svc.Hook.InitializeFromAttributes(this);
        LogInformation($"RMIWalk address: 0x{_rmiWalkHook.Address:X}");
        LogInformation($"RMIFly address: 0x{_rmiFlyHook.Address:X}");
        Svc.GameConfig.UiControlChanged += OnConfigChanged;
        UpdateLegacyMode();
    }

    public void Dispose()
    {
        Svc.GameConfig.UiControlChanged -= OnConfigChanged;
        _rmiWalkHook.Dispose();
        _rmiFlyHook.Dispose();
    }

    private void RMIWalkDetour(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk)
    {
        _rmiWalkHook.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);
        bool movementAllowed = bAdditiveUnk == 0 && !Svc.Condition[ConditionFlag.BeingMoved];
        if (movementAllowed && (IgnoreUserInput || *sumLeft == 0 && *sumForward == 0) && DirectionToDestination(false) is var relDir && relDir != null)
        {
            var dir = relDir.Value.h.ToDirection();
            *sumLeft = dir.X;
            *sumForward = dir.Y;
        }
    }

    private void RMIFlyDetour(void* self, PlayerMoveControllerFlyInput* result)
    {
        _rmiFlyHook.Original(self, result);
        var player = Svc.ClientState.LocalPlayer;
        if (player == null) return;

        if (AdvancedUnstuck?.IsRunning == true)
        {
            var backwardDir = new Angle(player.Rotation) + 180f.Degrees();
            var vector = backwardDir.ToDirection();

            result->Forward = 2f;
            result->Left = 0f;
            result->Up = -1f; // Optionally add a small Up value to help with ledges
            return;
        }
    }

    private (Angle h, Angle v)? DirectionToDestination(bool allowVertical)
    {
        var player = Svc.ClientState.LocalPlayer;
        if (player == null)
            return null;

        var dist = DesiredPosition - player.Position;
        if (dist.LengthSquared() <= Precision * Precision)
            return null;

        var dirH = Angle.FromDirectionXZ(dist);
        var dirV = allowVertical ? Angle.FromDirection(new(dist.Y, new Vector2(dist.X, dist.Z).Length())) : default;

        var refDir = _legacyMode
            ? ((CameraEx*)CameraManager.Instance()->GetActiveCamera())->DirH.Radians() + 180.Degrees()
            : player.Rotation.Radians();
        return (dirH - refDir, dirV);
    }

    private void OnConfigChanged(object? sender, ConfigChangeEvent evt) => UpdateLegacyMode();

    private void UpdateLegacyMode()
    {
        _legacyMode = Svc.GameConfig.UiControl.TryGetUInt("MoveMode", out var mode) && mode == 1;
        LogInformation($"Legacy mode is now {(_legacyMode ? "enabled" : "disabled")}");
    }

    private void LogInformation(string message)
    {
        // Replace this with your preferred logging system or Dalamud.Logger.Log
        PluginLog.Information(message);
    }
}
