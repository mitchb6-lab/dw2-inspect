using System;
using System.Globalization;

namespace Dw2MpShared;

/// <summary>The button a player pressed on the desync popup. Byte-sized because it goes on the wire.</summary>
public enum DesyncChoice : byte
{
    None = 0,
    SaveAndQuit = 1,
    AttemptResync = 2,
}

public enum DesyncPhase
{
    Idle,
    AwaitingChoice,
    Resyncing,
    Quitting,
}

/// <summary>Who diverged. In a host-authoritative session that is always a joiner, but the text says so rather than assuming it.</summary>
public sealed class DesyncWho
{
    public string PlayerName { get; set; } = "";

    public int Slot { get; set; } = -1;

    public bool IsHost { get; set; }

    public string Describe()
    {
        string role = IsHost ? "host" : "joiner";
        string name = string.IsNullOrWhiteSpace(PlayerName) ? ("the " + role) : PlayerName;

        if (Slot < 0)
        {
            return $"{name} ({role})";
        }

        return $"{name} ({role}, slot {Slot})";
    }
}

/// <summary>
/// The pure logic behind the in-game desync popup (mitch, in chat 2026-11-10): "if there is a
/// detected desync have an option come up that says who desynced. in the pop up lets have two
/// options save and quit 1 attempt resync 2".
///
/// One instance per process, mirrored on both peers through the DesyncChoice and DesyncOutcome
/// messages, so both popups offer the same buttons. No game types here: this file is compiled
/// into the mod and into src\Dw2MpTests, where it is tested.
/// </summary>
public sealed class DesyncPolicy
{
    /// <summary>After this many failed resyncs the popup offers only "Save and quit".</summary>
    public const int MaxResyncAttempts = 2;

    public const string Title = "DESYNC DETECTED";

    public const string SaveAndQuitLabel = "1. Save and quit";

    public const string AttemptResyncLabel = "2. Attempt resync";

    public DesyncPhase Phase { get; private set; } = DesyncPhase.Idle;

    public int FailedResyncs { get; private set; }

    public bool OffersResync => FailedResyncs < MaxResyncAttempts;

    /// <summary>The attempt the next "Attempt resync" press would be, 1-based.</summary>
    public int NextAttemptNumber => FailedResyncs + 1;

    /// <summary>A new desync. False when one is already being handled, so a second report does not stack a second popup.</summary>
    public bool Detect()
    {
        if (Phase != DesyncPhase.Idle)
        {
            return false;
        }

        Phase = DesyncPhase.AwaitingChoice;
        return true;
    }

    /// <summary>A button press, local or mirrored from the peer. False when the press is not valid now.</summary>
    public bool Choose(DesyncChoice choice)
    {
        if (Phase != DesyncPhase.AwaitingChoice)
        {
            return false;
        }

        if (choice == DesyncChoice.SaveAndQuit)
        {
            Phase = DesyncPhase.Quitting;
            return true;
        }

        if (choice == DesyncChoice.AttemptResync && OffersResync)
        {
            Phase = DesyncPhase.Resyncing;
            return true;
        }

        return false;
    }

    /// <summary>The resync was verified (or not). Success closes the incident; failure returns to the choice with one more failure on the counter.</summary>
    public void ResyncFinished(bool succeeded)
    {
        if (Phase != DesyncPhase.Resyncing)
        {
            return;
        }

        if (succeeded)
        {
            Reset();
            return;
        }

        FailedResyncs++;
        Phase = DesyncPhase.AwaitingChoice;
    }

    /// <summary>Adopt the peer's failure count when it is ahead of ours, so both popups agree on what is offered.</summary>
    public void Mirror(int failedResyncsOnPeer)
    {
        if (failedResyncsOnPeer > FailedResyncs)
        {
            FailedResyncs = failedResyncsOnPeer;
        }
    }

    public void Reset()
    {
        Phase = DesyncPhase.Idle;
        FailedResyncs = 0;
    }

    public static string Describe(DesyncWho who, string gameTime, long hostTick)
    {
        return $"{who.Describe()} is out of sync with the host's game at {gameTime} (host tick {hostTick}).";
    }

    public string OptionsText()
    {
        if (OffersResync)
        {
            return $"Choose: 1. Save and quit, or 2. Attempt resync (the host sends a fresh full state; attempt {NextAttemptNumber} of {MaxResyncAttempts}).";
        }

        return $"Both resync attempts failed. The only option left is 1. Save and quit.";
    }

    /// <summary>
    /// Desync-yyyyMMdd-HHmmss.DWGame: its own name, so it can never overwrite the harness's
    /// pinned save or one of the player's Autosave slots.
    /// </summary>
    public static string SaveFileName(DateTime now)
    {
        return "Desync-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".DWGame";
    }
}
