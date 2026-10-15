using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace WinShot.MainWindow.Uia;

internal static partial class Program
{
    private const string RootEnvironment = "WINSHOT_MAIN_UIA_ROOT";
    private const string ReadyEventEnvironment = "WINSHOT_MAIN_UIA_READY_EVENT";
    private const string ShutdownEventEnvironment = "WINSHOT_MAIN_UIA_SHUTDOWN_EVENT";
    private const string WidthEnvironment = "WINSHOT_MAIN_UIA_WIDTH";
    private const string RecentStripAutomationId = "RecentCaptureStrip";
    private static readonly string[] ExpectedRecentAutomationIds =
    [
        "RecentCapture_0",
        "RecentCapture_1",
        "RecentCapture_2",
        "RecentCapture_3",
        "RecentCapture_4",
    ];
    private const int ReadyTimeoutMilliseconds = 15_000;
    private const int WindowTimeoutMilliseconds = 10_000;
    private const int AcknowledgmentTimeoutMilliseconds = 10_000;
    private const int GracefulShutdownTimeoutMilliseconds = 10_000;
    private const int CleanupShutdownTimeoutMilliseconds = 2_000;
    private const int ForcedShutdownTimeoutMilliseconds = 5_000;
    private const ushort VirtualKeyTab = 0x09;
    private const ushort VirtualKeyReturn = 0x0D;
    private const ushort VirtualKeyShift = 0x10;
    private const ushort VirtualKeySpace = 0x20;
    private const ushort VirtualKeyLeftButton = 0x01;
    private const int InputCleanupAttempts = 4;
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint GetAncestorRoot = 2;

    private static int Main(string[] args)
    {
        var result = new HarnessResult();
        string? fixtureRoot = null;
        string? expectedExecutablePath = null;
        long expectedStartTimeUtcTicks = 0;
        Process? child = null;
        EventWaitHandle? readyEvent = null;
        EventWaitHandle? shutdownEvent = null;
        bool functionalChecksPassed = false;

        try
        {
            HarnessOptions options = ParseAndValidateOptions(args);
            string appPath = options.AppPath;
            result.WidthDip = options.WidthDip;
            fixtureRoot = CreateOwnedFixtureRoot();
            string token = Guid.NewGuid().ToString("N");
            string readyEventName = $@"Local\WinShot.MainWindowUia.Ready.{token}";
            string shutdownEventName = $@"Local\WinShot.MainWindowUia.Shutdown.{token}";

            readyEvent = CreateOwnedEvent(readyEventName, "ReadyEventCollision");
            shutdownEvent = CreateOwnedEvent(shutdownEventName, "ShutdownEventCollision");

            var startInfo = new ProcessStartInfo
            {
                FileName = appPath,
                WorkingDirectory = Path.GetDirectoryName(appPath)
                    ?? throw new HarnessFailureException("InvalidAppDirectory"),
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            startInfo.ArgumentList.Add("--ui-preview");
            startInfo.ArgumentList.Add("main-uia");
            startInfo.Environment[RootEnvironment] = fixtureRoot;
            startInfo.Environment[ReadyEventEnvironment] = readyEventName;
            startInfo.Environment[ShutdownEventEnvironment] = shutdownEventName;
            startInfo.Environment[WidthEnvironment] =
                options.WidthDip.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);

            child = Process.Start(startInfo)
                ?? throw new HarnessFailureException("ChildLaunchFailed");
            result.ChildPid = child.Id;
            child.Refresh();
            expectedExecutablePath = WaitForCanonicalProcessImagePath(child);
            expectedStartTimeUtcTicks = child.StartTime.ToUniversalTime().Ticks;
            Require(
                PathEquals(expectedExecutablePath, appPath),
                "InitialChildImageMismatch");
            ValidateExactChildIdentity(
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            result.ChildIdentityMatched = true;

            WaitForReady(
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks,
                readyEvent);
            nint windowHandle = WaitForOwnedMainWindow(
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            ValidateExactChildIdentity(
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            AutomationElement window = GetOwnedAutomationWindow(
                windowHandle,
                child.Id);
            ValidateWindowWidth(windowHandle, options.WidthDip, result);

            ValidateExactChildIdentity(
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            AutomationElement strip = FindUniqueByAutomationId(
                window,
                RecentStripAutomationId,
                child.Id,
                "RecentStripNotUnique");
            result.RecentStripFound = true;
            ValidatePrivateMetadata(
                strip,
                fixtureRoot,
                expectedExecutablePath,
                requireName: true,
                "RecentStripMetadataInvalid");

            IReadOnlyList<AutomationElement> recentButtons =
                GetOrderedRecentButtons(strip, child.Id);
            result.RecentButtonCount = recentButtons.Count;
            Require(
                recentButtons.Count == ExpectedRecentAutomationIds.Length,
                "RecentButtonCountMismatch");
            var validatedButtons = new List<AutomationElement>(
                ExpectedRecentAutomationIds.Length);
            for (int index = 0; index < ExpectedRecentAutomationIds.Length; index++)
            {
                string automationId = ExpectedRecentAutomationIds[index];
                Require(
                    string.Equals(
                        recentButtons[index].Current.AutomationId,
                        automationId,
                        StringComparison.Ordinal),
                    $"RecentOrderMismatch{index}");
                AutomationElement button = FindUniqueByAutomationId(
                    window,
                    automationId,
                    child.Id,
                    $"RecentNotUnique{index}");
                ValidateExactChildIdentity(
                    child,
                    expectedExecutablePath,
                    expectedStartTimeUtcTicks);
                ValidateRecentButton(
                    button,
                    child.Id,
                    fixtureRoot,
                    expectedExecutablePath,
                    $"RecentInvalid{index}");
                validatedButtons.Add(button);
            }
            result.ButtonsPassed = true;
            AutomationElement first = validatedButtons[0];

            string acknowledgmentPath = Path.Combine(
                fixtureRoot,
                "activation.ack");
            Require(
                !File.Exists(acknowledgmentPath),
                "ActivationAcknowledgmentWasNotEmpty");
            ValidateExactChildIdentity(
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            if (!first.TryGetCurrentPattern(
                    InvokePattern.Pattern,
                    out object? invokePatternObject)
                || invokePatternObject is not InvokePattern invokePattern)
            {
                throw new HarnessFailureException(
                    "FirstRecentInvokePatternMissing");
            }
            invokePattern.Invoke();
            result.InvokeAckCount = WaitForSingleAcknowledgment(
                acknowledgmentPath,
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            ValidateExactChildIdentity(
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            ExerciseRealInput(
                windowHandle,
                strip,
                validatedButtons,
                acknowledgmentPath,
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks,
                result);

            shutdownEvent.Set();
            result.ShutdownSignalSent = true;
            Require(
                child.WaitForExit(GracefulShutdownTimeoutMilliseconds),
                "GracefulShutdownTimeout");
            result.GracefulShutdown = true;
            result.ChildExited = true;
            functionalChecksPassed = true;
        }
        catch (HarnessFailureException ex)
        {
            SetFailure(result, ex.Code);
        }
        catch (UnauthorizedAccessException)
        {
            SetFailure(result, "AccessDenied");
        }
        catch (Exception)
        {
            SetFailure(result, "UnexpectedHarnessFailure");
        }
        finally
        {
            TryStopExactChild(
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks,
                shutdownEvent,
                result);

            shutdownEvent?.Dispose();
            readyEvent?.Dispose();
            child?.Dispose();

            if (fixtureRoot is not null)
            {
                if (result.ChildExited || result.ChildPid == 0)
                {
                    result.TemporaryDirectoryRemoved =
                        TryDeleteOwnedFixtureRoot(fixtureRoot);
                }
                else
                {
                    result.TemporaryDirectoryRemoved = false;
                }
            }

            if (!result.TemporaryDirectoryRemoved)
                SetFailure(result, "TemporaryDirectoryCleanupFailed");
            if (!result.ChildExited && result.ChildPid != 0)
                SetFailure(result, "ChildDidNotExit");

            result.OverallPassed =
                functionalChecksPassed
                && result.ChildIdentityMatched
                && result.WindowWidthMatched
                && result.RecentStripFound
                && result.RecentButtonCount == ExpectedRecentAutomationIds.Length
                && result.ButtonsPassed
                && result.InvokeAckCount == 1
                && result.FocusSequencePassed
                && result.BringIntoViewPassed
                && result.RealInputAckCount == 3
                && result.TotalAckCount == 4
                && result.RealInputPassed
                && result.ShutdownSignalSent
                && result.GracefulShutdown
                && !result.FallbackKillUsed
                && result.ChildExited
                && result.TemporaryDirectoryRemoved
                && string.Equals(
                    result.FailureCode,
                    "None",
                    StringComparison.Ordinal);

            Console.WriteLine(
                JsonSerializer.Serialize(
                    result,
                    HarnessJsonContext.Default.HarnessResult));
        }

        return result.OverallPassed ? 0 : 2;
    }

    private static HarnessOptions ParseAndValidateOptions(string[] args)
    {
        Require(
            args.Length == 4
            && string.Equals(args[0], "--app", StringComparison.Ordinal)
            && string.Equals(args[2], "--width", StringComparison.Ordinal),
            "InvalidArguments");
        string appPath = Path.GetFullPath(args[1]);
        Require(File.Exists(appPath), "AppNotFound");
        Require(
            string.Equals(
                Path.GetFileName(appPath),
                "WinShot.exe",
                StringComparison.OrdinalIgnoreCase),
            "UnexpectedAppFileName");
        Require(
            DebugAppPathRegex().IsMatch(appPath),
            "AppIsNotExpectedDebugOutput");
        Require(
            int.TryParse(
                args[3],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int widthDip)
            && widthDip is 640 or 760 or 900,
            "InvalidWidth");
        return new HarnessOptions(appPath, widthDip);
    }

    private static string CreateOwnedFixtureRoot()
    {
        string tempRoot = GetCanonicalTempRoot();
        for (int attempt = 0; attempt < 4; attempt++)
        {
            string candidate = Path.Combine(
                tempRoot,
                $"WinShotMainWindowUiaTests-{Guid.NewGuid():N}");
            if (Directory.Exists(candidate) || File.Exists(candidate))
                continue;
            Directory.CreateDirectory(candidate);
            ValidateOwnedFixtureRoot(candidate);
            return candidate;
        }

        throw new HarnessFailureException("FixtureRootCreationFailed");
    }

    private static EventWaitHandle CreateOwnedEvent(
        string eventName,
        string collisionFailureCode)
    {
        var handle = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            eventName,
            out bool createdNew);
        if (createdNew)
            return handle;
        handle.Dispose();
        throw new HarnessFailureException(collisionFailureCode);
    }

    private static void WaitForReady(
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks,
        EventWaitHandle readyEvent)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < ReadyTimeoutMilliseconds)
        {
            ValidateExactChildIdentity(
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            if (readyEvent.WaitOne(100))
                return;
        }

        throw new HarnessFailureException("ReadyEventTimeout");
    }

    private static nint WaitForOwnedMainWindow(
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < WindowTimeoutMilliseconds)
        {
            ValidateExactChildIdentity(
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            child.Refresh();
            nint handle = child.MainWindowHandle;
            if (handle != 0 && IsWindow(handle))
            {
                _ = GetWindowThreadProcessId(handle, out uint ownerProcessId);
                if (ownerProcessId == (uint)child.Id)
                    return handle;
            }
            Thread.Sleep(50);
        }

        throw new HarnessFailureException("OwnedMainWindowTimeout");
    }

    private static AutomationElement GetOwnedAutomationWindow(
        nint windowHandle,
        int expectedProcessId)
    {
        AutomationElement window = AutomationElement.FromHandle(windowHandle);
        Require(
            window.Current.ProcessId == expectedProcessId,
            "AutomationWindowOwnerMismatch");
        return window;
    }

    private static AutomationElement FindUniqueByAutomationId(
        AutomationElement root,
        string automationId,
        int processId,
        string failureCode)
    {
        var condition = new AndCondition(
            new PropertyCondition(
                AutomationElement.AutomationIdProperty,
                automationId),
            new PropertyCondition(
                AutomationElement.ProcessIdProperty,
                processId));
        AutomationElementCollection matches = root.FindAll(
            TreeScope.Descendants,
            condition);
        Require(matches.Count == 1, failureCode);
        return matches[0];
    }

    private static IReadOnlyList<AutomationElement> GetOrderedRecentButtons(
        AutomationElement strip,
        int processId)
    {
        var condition = new PropertyCondition(
            AutomationElement.ProcessIdProperty,
            processId);
        AutomationElementCollection descendants = strip.FindAll(
            TreeScope.Descendants,
            condition);
        var buttons = new List<AutomationElement>();
        for (int index = 0; index < descendants.Count; index++)
        {
            AutomationElement candidate = descendants[index];
            if (candidate.Current.AutomationId.StartsWith(
                    "RecentCapture_",
                    StringComparison.Ordinal))
            {
                buttons.Add(candidate);
            }
        }
        return buttons;
    }

    private static void ValidateRecentButton(
        AutomationElement button,
        int expectedProcessId,
        string fixtureRoot,
        string expectedExecutablePath,
        string failureCode)
    {
        Require(
            button.Current.ProcessId == expectedProcessId,
            failureCode + "Process");
        Require(
            button.Current.ControlType == ControlType.Button,
            failureCode + "ControlType");
        Require(button.Current.IsEnabled, failureCode + "Enabled");
        Require(
            button.Current.IsKeyboardFocusable,
            failureCode + "KeyboardFocusable");
        Require(
            button.TryGetCurrentPattern(
                InvokePattern.Pattern,
                out object? patternObject)
            && patternObject is InvokePattern,
            failureCode + "InvokePattern");
        ValidatePrivateMetadata(
            button,
            fixtureRoot,
            expectedExecutablePath,
            requireName: true,
            failureCode + "Metadata");
    }

    private static void ValidateWindowWidth(
        nint windowHandle,
        int widthDip,
        HarnessResult result)
    {
        Require(GetWindowRect(windowHandle, out NativeRect bounds), "WindowBoundsUnavailable");
        uint dpi = GetDpiForWindow(windowHandle);
        Require(dpi > 0, "WindowDpiUnavailable");
        int actualWidth = bounds.Right - bounds.Left;
        int expectedWidth = checked((int)Math.Round(
            widthDip * dpi / 96d,
            MidpointRounding.AwayFromZero));
        result.WindowDpi = checked((int)dpi);
        result.WindowWidthPixels = actualWidth;
        result.WindowWidthMatched = Math.Abs(actualWidth - expectedWidth) <= 2;
        Require(result.WindowWidthMatched, "WindowWidthMismatch");
    }

    private static void ExerciseRealInput(
        nint windowHandle,
        AutomationElement strip,
        IReadOnlyList<AutomationElement> buttons,
        string acknowledgmentPath,
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks,
        HarnessResult result)
    {
        Require(
            buttons.Count == ExpectedRecentAutomationIds.Length,
            "RealInputButtonCountMismatch");
        RequireInputsReleased(
            VirtualKeyTab,
            VirtualKeyReturn,
            VirtualKeyShift,
            VirtualKeySpace,
            VirtualKeyLeftButton);
        System.Windows.Rect stripBounds = strip.Current.BoundingRectangle;
        System.Windows.Rect initialFifthBounds =
            buttons[^1].Current.BoundingRectangle;
        bool fifthInitiallyFullyVisible =
            IsFullyWithin(initialFifthBounds, stripBounds);

        FocusAndWait(
            buttons[0],
            ExpectedRecentAutomationIds[0],
            windowHandle,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        for (int index = 1; index < ExpectedRecentAutomationIds.Length; index++)
        {
            SendKey(
                windowHandle,
                VirtualKeyTab,
                ExpectedRecentAutomationIds[index - 1],
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            WaitForFocusedAutomationId(
                ExpectedRecentAutomationIds[index],
                child.Id);
        }

        System.Windows.Rect focusedFifthBounds =
            buttons[^1].Current.BoundingRectangle;
        bool fifthFullyVisible =
            IsFullyWithin(focusedFifthBounds, strip.Current.BoundingRectangle);
        bool horizontalMovementObserved =
            Math.Abs(focusedFifthBounds.Left - initialFifthBounds.Left) > 0.5;
        result.BringIntoViewPassed =
            fifthFullyVisible
            && (fifthInitiallyFullyVisible || horizontalMovementObserved);
        Require(result.BringIntoViewPassed, "RecentBringIntoViewFailed");

        for (int index = ExpectedRecentAutomationIds.Length - 2; index >= 0; index--)
        {
            SendShiftTab(
                windowHandle,
                ExpectedRecentAutomationIds[index + 1],
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            WaitForFocusedAutomationId(
                ExpectedRecentAutomationIds[index],
                child.Id);
        }
        result.FocusSequencePassed = true;

        FocusAndWait(
            buttons[0],
            ExpectedRecentAutomationIds[0],
            windowHandle,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        SendKey(
            windowHandle,
            VirtualKeyReturn,
            ExpectedRecentAutomationIds[0],
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        WaitForAcknowledgmentCount(
            acknowledgmentPath,
            expectedCount: 2,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);

        FocusAndWait(
            buttons[1],
            ExpectedRecentAutomationIds[1],
            windowHandle,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        SendKey(
            windowHandle,
            VirtualKeySpace,
            ExpectedRecentAutomationIds[1],
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        WaitForAcknowledgmentCount(
            acknowledgmentPath,
            expectedCount: 3,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);

        ClickButton(
            windowHandle,
            buttons[2],
            ExpectedRecentAutomationIds[2],
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        result.TotalAckCount = WaitForAcknowledgmentCount(
            acknowledgmentPath,
            expectedCount: 4,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        result.RealInputAckCount = result.TotalAckCount - result.InvokeAckCount;
        result.RealInputPassed =
            result.FocusSequencePassed
            && result.BringIntoViewPassed
            && result.RealInputAckCount == 3;
        Require(result.RealInputPassed, "RealInputFailed");
    }

    private static bool IsFullyWithin(
        System.Windows.Rect candidate,
        System.Windows.Rect container)
    {
        const double tolerance = 2;
        return !candidate.IsEmpty
            && !container.IsEmpty
            && candidate.Left >= container.Left - tolerance
            && candidate.Top >= container.Top - tolerance
            && candidate.Right <= container.Right + tolerance
            && candidate.Bottom <= container.Bottom + tolerance;
    }

    private static void FocusAndWait(
        AutomationElement element,
        string expectedAutomationId,
        nint windowHandle,
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks)
    {
        EnsureExactForeground(
            windowHandle,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        element.SetFocus();
        WaitForFocusedAutomationId(expectedAutomationId, child.Id);
    }

    private static void WaitForFocusedAutomationId(
        string expectedAutomationId,
        int expectedProcessId)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < WindowTimeoutMilliseconds)
        {
            try
            {
                AutomationElement focused = AutomationElement.FocusedElement;
                if (focused.Current.ProcessId == expectedProcessId
                    && string.Equals(
                        focused.Current.AutomationId,
                        expectedAutomationId,
                        StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            Thread.Sleep(25);
        }

        throw new HarnessFailureException(
            "FocusedElementMismatch" + expectedAutomationId);
    }

    private static void EnsureExactForeground(
        nint windowHandle,
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks)
    {
        ValidateExactInputWindow(
            windowHandle,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks,
            requireForeground: false);
        if (GetForegroundWindow() != windowHandle)
            Require(SetForegroundWindow(windowHandle), "ForegroundActivationFailed");

        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < WindowTimeoutMilliseconds)
        {
            if (GetForegroundWindow() == windowHandle)
            {
                ValidateExactInputWindow(
                    windowHandle,
                    child,
                    expectedExecutablePath,
                    expectedStartTimeUtcTicks,
                    requireForeground: true);
                return;
            }
            Thread.Sleep(25);
        }

        throw new HarnessFailureException("ForegroundWindowMismatch");
    }

    private static void ValidateExactInputWindow(
        nint windowHandle,
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks,
        bool requireForeground)
    {
        ValidateExactChildIdentity(
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        Require(IsWindow(windowHandle), "InputWindowUnavailable");
        _ = GetWindowThreadProcessId(windowHandle, out uint ownerProcessId);
        Require(
            ownerProcessId == (uint)child.Id,
            "InputWindowOwnerMismatch");
        AutomationElement window = AutomationElement.FromHandle(windowHandle);
        Require(
            window.Current.ProcessId == child.Id,
            "InputAutomationWindowOwnerMismatch");
        if (requireForeground)
        {
            Require(
                GetForegroundWindow() == windowHandle,
                "ForegroundWindowMismatch");
        }
    }

    private static void ValidateExactKeyTarget(
        nint windowHandle,
        string expectedAutomationId,
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks)
    {
        ValidateExactInputWindow(
            windowHandle,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks,
            requireForeground: true);
        AutomationElement focused = AutomationElement.FocusedElement;
        Require(
            focused.Current.ProcessId == child.Id
            && focused.Current.ControlType == ControlType.Button
            && string.Equals(
                focused.Current.AutomationId,
                expectedAutomationId,
                StringComparison.Ordinal),
            "KeyboardInputTargetMismatch");
    }

    private static void SendKey(
        nint windowHandle,
        ushort virtualKey,
        string expectedAutomationId,
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks)
    {
        RequireInputsReleased(virtualKey);
        EnsureExactForeground(
            windowHandle,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        ValidateExactKeyTarget(
            windowHandle,
            expectedAutomationId,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        bool keyDownInjected = false;
        try
        {
            keyDownInjected = TrySendSingleInput(
                CreateKeyboardInput(virtualKey, keyUp: false));
            Require(keyDownInjected, "KeyboardInputFailed");
        }
        finally
        {
            if (keyDownInjected)
            {
                Require(
                    TryReleaseKeyboardInput(virtualKey),
                    "KeyboardInputReleaseFailed");
            }
        }
    }

    private static void SendShiftTab(
        nint windowHandle,
        string expectedAutomationId,
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks)
    {
        RequireInputsReleased(VirtualKeyShift, VirtualKeyTab);
        EnsureExactForeground(
            windowHandle,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        ValidateExactKeyTarget(
            windowHandle,
            expectedAutomationId,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        bool shiftDownInjected = false;
        bool tabDownInjected = false;
        try
        {
            shiftDownInjected = TrySendSingleInput(
                CreateKeyboardInput(VirtualKeyShift, keyUp: false));
            Require(shiftDownInjected, "ShiftInputFailed");
            ValidateExactKeyTarget(
                windowHandle,
                expectedAutomationId,
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            tabDownInjected = TrySendSingleInput(
                CreateKeyboardInput(VirtualKeyTab, keyUp: false));
            Require(tabDownInjected, "ShiftTabInputFailed");
        }
        finally
        {
            bool tabReleased =
                !tabDownInjected || TryReleaseKeyboardInput(VirtualKeyTab);
            bool shiftReleased =
                !shiftDownInjected || TryReleaseKeyboardInput(VirtualKeyShift);
            Require(
                tabReleased && shiftReleased,
                "ShiftTabInputReleaseFailed");
        }
    }

    private static void SendSingleInput(
        NativeInput input,
        string failureCode)
    {
        Require(TrySendSingleInput(input), failureCode);
    }

    private static bool TrySendSingleInput(NativeInput input)
    {
        return SendInput(
            1,
            [input],
            Marshal.SizeOf<NativeInput>()) == 1;
    }

    private static bool TryReleaseKeyboardInput(ushort virtualKey)
    {
        for (int attempt = 0; attempt < InputCleanupAttempts; attempt++)
        {
            bool sent = TrySendSingleInput(
                CreateKeyboardInput(virtualKey, keyUp: true));
            Thread.Sleep(25);
            if (sent && !IsVirtualKeyDown(virtualKey))
                return true;
        }
        return false;
    }

    private static bool TryReleaseLeftButton()
    {
        NativeInput leftUp = new()
        {
            Type = InputMouse,
            Union = new NativeInputUnion
            {
                Mouse = new NativeMouseInput { Flags = MouseEventLeftUp },
            },
        };
        for (int attempt = 0; attempt < InputCleanupAttempts; attempt++)
        {
            bool sent = TrySendSingleInput(leftUp);
            Thread.Sleep(25);
            if (sent && !IsVirtualKeyDown(VirtualKeyLeftButton))
                return true;
        }
        return false;
    }

    private static bool TryRestoreCursor(NativePoint expected)
    {
        for (int attempt = 0; attempt < InputCleanupAttempts; attempt++)
        {
            bool positioned = SetCursorPos(expected.X, expected.Y);
            Thread.Sleep(25);
            if (positioned
                && GetCursorPos(out NativePoint actual)
                && actual.X == expected.X
                && actual.Y == expected.Y)
            {
                return true;
            }
        }
        return false;
    }

    private static void RequireInputsReleased(params ushort[] virtualKeys)
    {
        Require(
            virtualKeys.All(virtualKey => !IsVirtualKeyDown(virtualKey)),
            "InjectedInputAlreadyHeld");
    }

    private static bool IsVirtualKeyDown(ushort virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static NativeInput CreateKeyboardInput(
        ushort virtualKey,
        bool keyUp)
    {
        return new NativeInput
        {
            Type = InputKeyboard,
            Union = new NativeInputUnion
            {
                Keyboard = new NativeKeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0,
                },
            },
        };
    }

    private static void ClickButton(
        nint windowHandle,
        AutomationElement button,
        string expectedAutomationId,
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks)
    {
        RequireInputsReleased(VirtualKeyLeftButton);
        EnsureExactForeground(
            windowHandle,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
        Require(
            button.TryGetClickablePoint(out System.Windows.Point clickablePoint),
            "RecentClickablePointUnavailable");
        int x = checked((int)Math.Round(clickablePoint.X));
        int y = checked((int)Math.Round(clickablePoint.Y));
        System.Windows.Rect bounds = button.Current.BoundingRectangle;
        Require(bounds.Contains(clickablePoint), "RecentClickablePointOutsideButton");
        Require(GetCursorPos(out NativePoint originalCursor), "CursorReadFailed");
        bool leftDownInjected = false;
        try
        {
            Require(SetCursorPos(x, y), "PointerPositionFailed");
            Require(
                GetCursorPos(out NativePoint actualCursor)
                && actualCursor.X == x
                && actualCursor.Y == y,
                "PointerPositionMismatch");
            ValidateExactPointerTarget(
                windowHandle,
                x,
                y,
                expectedAutomationId,
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            ValidateExactPointerTarget(
                windowHandle,
                x,
                y,
                expectedAutomationId,
                child,
                expectedExecutablePath,
                expectedStartTimeUtcTicks);
            leftDownInjected = TrySendSingleInput(
                new NativeInput
                {
                    Type = InputMouse,
                    Union = new NativeInputUnion
                    {
                        Mouse = new NativeMouseInput
                        {
                            Flags = MouseEventLeftDown,
                        },
                    },
                });
            Require(leftDownInjected, "PointerInputFailed");
        }
        finally
        {
            bool buttonReleased =
                !leftDownInjected || TryReleaseLeftButton();
            bool cursorRestored = TryRestoreCursor(originalCursor);
            Require(
                buttonReleased && cursorRestored,
                "PointerInputCleanupFailed");
        }
        WaitForFocusedAutomationId(expectedAutomationId, child.Id);
    }

    private static void ValidateExactPointerTarget(
        nint windowHandle,
        int expectedX,
        int expectedY,
        string expectedAutomationId,
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks)
    {
        Require(
            GetCursorPos(out NativePoint actualCursor)
            && actualCursor.X == expectedX
            && actualCursor.Y == expectedY,
            "PointerPositionMismatch");
        ValidateExactInputWindow(
            windowHandle,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks,
            requireForeground: true);
        nint target = WindowFromPoint(actualCursor);
        Require(target != 0, "PointerTargetUnavailable");
        Require(
            GetAncestor(target, GetAncestorRoot) == windowHandle,
            "PointerTargetWindowMismatch");
        Require(
            IsAutomationPointWithinButton(
                new System.Windows.Point(actualCursor.X, actualCursor.Y),
                expectedAutomationId,
                child.Id),
            "PointerAutomationTargetMismatch");
    }

    private static bool IsAutomationPointWithinButton(
        System.Windows.Point point,
        string expectedAutomationId,
        int expectedProcessId)
    {
        AutomationElement? current;
        try
        {
            current = AutomationElement.FromPoint(point);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }

        for (int depth = 0; current is not null && depth < 16; depth++)
        {
            try
            {
                if (current.Current.ProcessId == expectedProcessId
                    && current.Current.ControlType == ControlType.Button
                    && string.Equals(
                        current.Current.AutomationId,
                        expectedAutomationId,
                        StringComparison.Ordinal))
                {
                    return true;
                }
                current = TreeWalker.RawViewWalker.GetParent(current);
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }
        return false;
    }

    private static void ValidatePrivateMetadata(
        AutomationElement element,
        string fixtureRoot,
        string expectedExecutablePath,
        bool requireName,
        string failureCode)
    {
        string name = element.Current.Name ?? "";
        string helpText = element.Current.HelpText ?? "";
        string itemStatus = element.Current.ItemStatus ?? "";
        if (requireName)
            Require(!string.IsNullOrWhiteSpace(name), failureCode);

        foreach (string value in new[] { name, helpText, itemStatus })
        {
            Require(
                IsPathPrivate(value, fixtureRoot, expectedExecutablePath),
                failureCode);
        }
    }

    private static bool IsPathPrivate(
        string value,
        string fixtureRoot,
        string expectedExecutablePath)
    {
        if (string.IsNullOrEmpty(value))
            return true;
        string appDirectory = Path.GetDirectoryName(expectedExecutablePath) ?? "";
        if (value.Contains(fixtureRoot, StringComparison.OrdinalIgnoreCase)
            || value.Contains(
                Path.GetFileName(fixtureRoot),
                StringComparison.OrdinalIgnoreCase)
            || value.Contains(
                expectedExecutablePath,
                StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(appDirectory)
                && value.Contains(
                    appDirectory,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !PathLikeMetadataRegex().IsMatch(value)
            && !ImageFileNameRegex().IsMatch(value);
    }

    private static int WaitForSingleAcknowledgment(
        string acknowledgmentPath,
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks)
    {
        return WaitForAcknowledgmentCount(
            acknowledgmentPath,
            expectedCount: 1,
            child,
            expectedExecutablePath,
            expectedStartTimeUtcTicks);
    }

    private static int WaitForAcknowledgmentCount(
        string acknowledgmentPath,
        int expectedCount,
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < AcknowledgmentTimeoutMilliseconds)
        {
            ValidateExactChildIdentity(
                child,
                expectedExecutablePath,
            expectedStartTimeUtcTicks);
            if (TryReadAcknowledgment(acknowledgmentPath, out string[] lines))
            {
                Require(
                    lines.Length <= expectedCount,
                    "DuplicateActivationAcknowledgment");
                if (lines.Length == expectedCount)
                {
                    Require(
                        lines.All(IsOpaqueAcknowledgment),
                        "InvalidActivationAcknowledgment");
                    Thread.Sleep(200);
                    Require(
                        TryReadAcknowledgment(
                            acknowledgmentPath,
                            out string[] settledLines)
                        && settledLines.SequenceEqual(
                            lines,
                            StringComparer.Ordinal),
                        "DuplicateActivationAcknowledgment");
                    return expectedCount;
                }
            }
            Thread.Sleep(50);
        }

        throw new HarnessFailureException("ActivationAcknowledgmentTimeout");
    }

    private static bool TryReadAcknowledgment(
        string path,
        out string[] lines)
    {
        lines = [];
        if (!File.Exists(path))
            return false;
        try
        {
            lines = File.ReadAllLines(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsOpaqueAcknowledgment(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && !value.Contains('\\')
            && !value.Contains('/')
            && !value.Contains(':')
            && !value.Contains('\r')
            && !value.Contains('\n');
    }

    private static string GetCanonicalProcessImagePath(Process process)
    {
        process.Refresh();
        string? imagePath = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new HarnessFailureException("ChildImageUnavailable");
        return Path.GetFullPath(imagePath);
    }

    private static string WaitForCanonicalProcessImagePath(Process process)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < WindowTimeoutMilliseconds)
        {
            if (process.HasExited)
                throw new HarnessFailureException("ChildExitedBeforeIdentity");
            try
            {
                return GetCanonicalProcessImagePath(process);
            }
            catch (HarnessFailureException ex)
                when (string.Equals(
                    ex.Code,
                    "ChildImageUnavailable",
                    StringComparison.Ordinal))
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            Thread.Sleep(25);
        }

        throw new HarnessFailureException("ChildImageUnavailable");
    }

    private static void ValidateExactChildIdentity(
        Process child,
        string expectedExecutablePath,
        long expectedStartTimeUtcTicks)
    {
        Require(!child.HasExited, "ChildExitedUnexpectedly");
        string actualPath = GetCanonicalProcessImagePath(child);
        long actualStartTimeUtcTicks =
            child.StartTime.ToUniversalTime().Ticks;
        Require(
            PathEquals(actualPath, expectedExecutablePath),
            "ChildImageChanged");
        Require(
            actualStartTimeUtcTicks == expectedStartTimeUtcTicks,
            "ChildStartTimeChanged");
    }

    private static bool IsExactChildIdentity(
        Process child,
        string? expectedExecutablePath,
        long expectedStartTimeUtcTicks)
    {
        if (string.IsNullOrWhiteSpace(expectedExecutablePath)
            || expectedStartTimeUtcTicks == 0)
        {
            return false;
        }

        try
        {
            if (child.HasExited)
                return false;
            return PathEquals(
                    GetCanonicalProcessImagePath(child),
                    expectedExecutablePath)
                && child.StartTime.ToUniversalTime().Ticks
                    == expectedStartTimeUtcTicks;
        }
        catch
        {
            return false;
        }
    }

    private static void TryStopExactChild(
        Process? child,
        string? expectedExecutablePath,
        long expectedStartTimeUtcTicks,
        EventWaitHandle? shutdownEvent,
        HarnessResult result)
    {
        if (child is null)
            return;

        try
        {
            if (child.HasExited)
            {
                result.ChildExited = true;
                return;
            }

            shutdownEvent?.Set();
            result.ShutdownSignalSent = shutdownEvent is not null;
            if (child.WaitForExit(CleanupShutdownTimeoutMilliseconds))
            {
                result.ChildExited = true;
                return;
            }

            if (!IsExactChildIdentity(
                    child,
                    expectedExecutablePath,
                    expectedStartTimeUtcTicks))
            {
                if (child.HasExited)
                {
                    result.ChildExited = true;
                    return;
                }
                result.ChildIdentityMatched = false;
                SetFailure(result, "CleanupChildIdentityMismatch");
                return;
            }

            result.FallbackKillUsed = true;
            child.Kill(entireProcessTree: false);
            result.ChildExited =
                child.WaitForExit(ForcedShutdownTimeoutMilliseconds);
            if (!result.ChildExited)
                SetFailure(result, "ForcedShutdownTimeout");
        }
        catch
        {
            try
            {
                result.ChildExited = child.HasExited;
            }
            catch
            {
                result.ChildExited = false;
            }
            SetFailure(result, "ChildCleanupFailed");
        }
    }

    private static bool TryDeleteOwnedFixtureRoot(string fixtureRoot)
    {
        try
        {
            string canonicalRoot = ValidateOwnedFixtureRoot(fixtureRoot);
            if (!Directory.Exists(canonicalRoot))
                return true;
            if (ContainsReparsePoint(canonicalRoot))
                return false;
            Directory.Delete(canonicalRoot, recursive: true);
            return !Directory.Exists(canonicalRoot)
                && !File.Exists(canonicalRoot);
        }
        catch
        {
            return false;
        }
    }

    private static string ValidateOwnedFixtureRoot(string fixtureRoot)
    {
        string canonicalRoot = Path.GetFullPath(fixtureRoot)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        string tempRoot = GetCanonicalTempRoot();
        string? parent = Path.GetDirectoryName(canonicalRoot);
        Require(
            parent is not null && PathEquals(parent, tempRoot),
            "FixtureRootParentMismatch");
        Require(
            FixtureLeafRegex().IsMatch(Path.GetFileName(canonicalRoot)),
            "FixtureRootNameMismatch");
        return canonicalRoot;
    }

    private static string GetCanonicalTempRoot()
    {
        return Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
    }

    private static bool ContainsReparsePoint(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            return true;

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
        return false;
    }

    private static bool PathEquals(string left, string right)
    {
        string canonicalLeft = Path.GetFullPath(left)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        string canonicalRight = Path.GetFullPath(right)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        return string.Equals(
            canonicalLeft,
            canonicalRight,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void SetFailure(HarnessResult result, string code)
    {
        if (string.Equals(
                result.FailureCode,
                "None",
                StringComparison.Ordinal))
        {
            result.FailureCode = code;
        }
    }

    private static void Require(bool condition, string failureCode)
    {
        if (!condition)
            throw new HarnessFailureException(failureCode);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] NativeInput[] inputs,
        int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint windowHandle,
        out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [GeneratedRegex(
        @"\\bin\\x64\\Debug\\net10\.0-windows10\.0\.19041\.0\\WinShot\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DebugAppPathRegex();

    [GeneratedRegex(
        @"^WinShotMainWindowUiaTests-[0-9a-f]{32}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex FixtureLeafRegex();

    [GeneratedRegex(
        @"(?:[a-z]:\\|\\\\|file://|(?:^|[\s""'(])/[^\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathLikeMetadataRegex();

    [GeneratedRegex(
        @"\.(?:png|jpe?g|gif|bmp|webp|tiff?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageFileNameRegex();

    private readonly record struct HarnessOptions(
        string AppPath,
        int WidthDip);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        internal uint Type;
        internal NativeInputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)]
        internal NativeMouseInput Mouse;

        [FieldOffset(0)]
        internal NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    private sealed class HarnessFailureException(string code) : Exception
    {
        internal string Code { get; } = code;
    }
}

internal sealed class HarnessResult
{
    public int SchemaVersion { get; init; } = 3;
    public string EvidenceKind { get; init; } = "WinShotMainWindowUiAutomation";
    public int ChildPid { get; set; }
    public bool ChildIdentityMatched { get; set; }
    public int WidthDip { get; set; }
    public int WindowDpi { get; set; }
    public int WindowWidthPixels { get; set; }
    public bool WindowWidthMatched { get; set; }
    public bool RecentStripFound { get; set; }
    public int RecentButtonCount { get; set; }
    public bool ButtonsPassed { get; set; }
    public int InvokeAckCount { get; set; }
    public bool FocusSequencePassed { get; set; }
    public bool BringIntoViewPassed { get; set; }
    public int RealInputAckCount { get; set; }
    public int TotalAckCount { get; set; }
    public bool RealInputPassed { get; set; }
    public bool ShutdownSignalSent { get; set; }
    public bool GracefulShutdown { get; set; }
    public bool FallbackKillUsed { get; set; }
    public bool ChildExited { get; set; }
    public bool TemporaryDirectoryRemoved { get; set; }
    public string FailureCode { get; set; } = "None";
    public bool OverallPassed { get; set; }
}

[JsonSerializable(typeof(HarnessResult))]
internal sealed partial class HarnessJsonContext : JsonSerializerContext;
