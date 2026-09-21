// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FortniteVideoSoftware.App.Abstractions;
using FortniteVideoSoftware.App.Services;
using FortniteVideoSoftware.Core.Abstractions;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.App.Infrastructure;

/// <summary>
/// COMPOSITION_01 — THE ONE PLACE THE OBJECT GRAPH IS BUILT.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// <b>WHY A HAND-WRITTEN ROOT AND NOT A DI CONTAINER.</b> An audit found <b>3 interfaces and 183
/// static mutable members</b> across ~90,000 lines, with every collaborator resolved by
/// <c>new</c>-ing it at its use site or by reaching for a static. That is the reason the App layer
/// has no tests: there is nowhere to substitute anything.
///
/// The obvious fix is <c>Microsoft.Extensions.DependencyInjection</c>. It is rejected here for two
/// specific reasons, not out of preference:
///   1. <b>Mandate #1 — single binary, NativeAOT, <c>TrimMode=full</c>.</b> A container resolves by
///      <see cref="Type"/> at run time. Every resolution is a trim-analyser liability, and the
///      project's own <c>AOTSAFETY_01</c> block says warnings here are fixed, never muted.
///   2. <b>A container hides the graph.</b> This one is ~30 lines and can be read in full. When
///      the question is "what does MainWindow actually depend on", a file you can read beats a
///      registration list you have to execute.
/// Constructing everything explicitly costs one line per service and buys compile-time proof that
/// the graph is complete.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </para>
///
/// <para>
/// <b>⚠️ COMPOSITION_02 — <see cref="Current"/> IS A MIGRATION SHIM WITH A DELETION DATE.</b>
/// A static accessor is a service locator, and a service locator is the anti-pattern this class
/// exists to retire. It is here because ~25,000 lines of existing code-behind are constructed by
/// Avalonia's own lifetime and by XAML, neither of which can pass constructor arguments. Rewriting
/// all of that in the same change that introduces the root would make the change unreviewable.
///
/// The contract for the transition is therefore explicit:
///   • NEW code — every view-model, every service — takes its dependencies as CONSTRUCTOR
///     PARAMETERS. It must never touch <see cref="Current"/>.
///   • LEGACY window code-behind may read <see cref="Current"/> until its view-model is extracted.
///   • <see cref="Current"/> is DELETED at the end of the view-model extraction phase. The
///     architecture test <c>NoNewServiceLocatorUsage</c> holds the line by failing the build if the
///     number of call sites ever goes UP.
/// </para>
/// </summary>
public sealed class AppServices
{
    private static AppServices? _current;

    /// <summary>
    /// ⚠️ MIGRATION SHIM — see COMPOSITION_02 on the class. Legacy code-behind only.
    /// New code takes an <see cref="AppServices"/> (or the individual interface) in its constructor.
    /// </summary>
    public static AppServices Current
        => _current ?? throw new InvalidOperationException(
            "AppServices.Current was read before Initialize() ran. The composition root is built "
          + "once in Program.RunUiAsync, before Avalonia starts. If you are seeing this from a "
          + "test, call AppServices.InitializeForTests(...) in the fixture.");

    /// <summary>True once <see cref="Initialize"/> has run. Lets teardown paths avoid the throw above.</summary>
    public static bool IsInitialized => _current is not null;

    // ── The graph ────────────────────────────────────────────────────────────────────────────

    /// <summary>ProgramData, temp and log path resolution (05 §SYS-MUTEX / GOV).</summary>
    public ApplicationPaths Paths { get; }

    /// <summary>The wall clock, behind a seam (INJSEAM_02).</summary>
    public IClock Clock { get; }

    /// <summary>Which window a notice or dialog belongs to (INJSEAM_03).</summary>
    public IActiveWindowProvider Windows { get; }

    /// <summary>Pills, alerts and questions (INJSEAM_03).</summary>
    public IUserNotifier Notifier { get; }

    /// <summary>
    /// THE destination for every caught exception (FAULTTIER_01).
    /// A <c>catch</c> block that does not end here is a defect the architecture tests will flag.
    /// </summary>
    public IFaultSink Faults { get; }

    /// <summary>Reading and writing <c>.fvsproj</c> (INJSEAM_01 / PROJ_08).</summary>
    public IProjectStore Projects { get; }

    /// <summary>Native save/open dialogs plus their directory memory (INJSEAM_04).</summary>
    public IFilePickerService FilePicker { get; }

    private AppServices(
        ApplicationPaths paths,
        IClock clock,
        IActiveWindowProvider windows,
        IUserNotifier notifier,
        IFaultSink faults,
        IProjectStore projects,
        IFilePickerService filePicker)
    {
        Paths = paths;
        Clock = clock;
        Windows = windows;
        Notifier = notifier;
        Faults = faults;
        Projects = projects;
        FilePicker = filePicker;
    }

    /// <summary>
    /// Builds the production graph. Called exactly once, from <c>Program.RunUiAsync</c>, AFTER
    /// <c>BootstrapAsync</c> has resolved and created the ProgramData directories and BEFORE
    /// <c>AppBuilder.StartWithClassicDesktopLifetime</c> hands control to Avalonia.
    ///
    /// <para>The ordering matters: <see cref="ApplicationPaths.EnsureWritableDirectories"/> must
    /// already have run, and no window may exist yet — <see cref="AvaloniaWindowProvider"/> reads
    /// the desktop lifetime lazily, so it is safe to construct before there is anything to find.</para>
    /// </summary>
    public static AppServices Initialize(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (_current is not null)
        {
            // Not fatal, but it means two roots exist and half the app is talking to the wrong one.
            RuntimeLog.Fail("COMPOSITION",
                "AppServices.Initialize called twice. The second call is ignored; the first graph stands.");
            return _current;
        }

        var windows  = new AvaloniaWindowProvider();
        var notifier = new AvaloniaUserNotifier(windows);
        var faults   = new UserFacingFaultSink(notifier);

        _current = new AppServices(
            paths:      paths,
            clock:      SystemClock.Instance,
            windows:    windows,
            notifier:   notifier,
            faults:     faults,
            projects:   FileProjectStore.Instance,
            filePicker: new StorageProviderFilePicker(windows, faults));

        // FAULTTIER_02 — make the sink reachable from the ~900 catch blocks that cannot be handed
        // one: static helpers, window code-behind Avalonia constructs, worker threads with no
        // object graph in scope. Installed HERE, immediately after the graph is built, because
        // every line of startup after this point can now report a classified failure instead of
        // writing a log line nobody opens.
        //
        // ⚠️ This is a diagnostic channel, not a collaborator. See the note on Faults for why that
        // distinction is what keeps it from being the service locator COMPOSITION_02 retires.
        Core.Abstractions.Faults.Install(faults);

        RuntimeLog.Info("COMPOSITION", "Application service graph constructed.");
        return _current;
    }

    /// <summary>
    /// Test seam: installs a graph assembled from fakes. Mirrors <see cref="Initialize"/> but takes
    /// every collaborator, so a test can supply a recording notifier and an in-memory project store.
    /// </summary>
    public static AppServices InitializeForTests(
        ApplicationPaths paths,
        IClock clock,
        IActiveWindowProvider windows,
        IUserNotifier notifier,
        IFaultSink faults,
        IProjectStore projects,
        IFilePickerService filePicker)
    {
        _current = new AppServices(paths, clock, windows, notifier, faults, projects, filePicker);

        // FAULTTIER_02 — a test that installs a recording sink must also receive the faults raised
        // through the ambient channel, or half the code under test reports into a void and the
        // test passes while proving nothing.
        Core.Abstractions.Faults.ResetForTests();
        Core.Abstractions.Faults.Install(faults);

        return _current;
    }

    /// <summary>Test teardown. Never called in production — the graph lives as long as the process.</summary>
    public static void ResetForTests()
    {
        _current = null;
        Core.Abstractions.Faults.ResetForTests();
    }
}

/// <summary>
/// INJSEAM_03 — resolves "the window a message belongs to" from Avalonia's desktop lifetime.
///
/// <para>
/// Preference order, and the reason for each:
///   1. The window that currently has OS focus. A notice about an operation the user just started
///      belongs on the window they are looking at.
///   2. <c>MainWindow</c>. The suite's companion windows can close while their background work is
///      still finishing; the main window is the last one standing.
///   3. Any open window at all.
///   4. <c>null</c> — startup, or after the last window closed. Callers MUST tolerate this and fall
///      back to the log. Throwing here would make the fault reporter itself a source of faults.
/// </para>
/// </summary>
public sealed class AvaloniaWindowProvider : IActiveWindowProvider
{
    public Window? ActiveWindow
    {
        get
        {
            try
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                    return null;

                foreach (Window w in desktop.Windows)
                    if (w.IsActive) return w;

                if (desktop.MainWindow is { } main) return main;

                foreach (Window w in desktop.Windows)
                    return w;

                return null;
            }
            catch (Exception ex)
            {
                // Reading the lifetime during teardown can race. A null answer degrades the caller
                // to the log, which is the documented contract above.
                RuntimeLog.Debug("COMPOSITION", $"Active window lookup failed: {ex.Message}");
                return null;
            }
        }
    }
}
