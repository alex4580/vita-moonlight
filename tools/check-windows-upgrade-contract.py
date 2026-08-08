#!/usr/bin/env python3
"""Statically verify the safe Windows in-place-upgrade compatibility contract.

The installer must run against older installed host binaries before it can
replace them.  Those binaries cannot be assumed to expose commands introduced
by the candidate.  This check records the command surface of real legacy
builds and verifies that pre-replacement setup uses only that common surface.

This is deliberately a source-only check: it never queries or changes the live
Windows display topology, services, scheduled tasks, registry, or installation.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path
from typing import List


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
INSTALLER_PATH = REPOSITORY_ROOT / "host/installer/VitaMoonlightHost.iss"
MAINTENANCE_PATH = (
    REPOSITORY_ROOT / "host/VitaMoonlight.Host/InstallerMaintenanceFence.cs"
)
UNINSTALL_PATH = REPOSITORY_ROOT / "host/VitaMoonlight.Host/UninstallManager.cs"
RECOVERY_TASK_PATH = (
    REPOSITORY_ROOT / "host/VitaMoonlight.Host/RecoveryTaskManager.cs"
)
RESCUE_AGENT_PATH = (
    REPOSITORY_ROOT / "host/VitaMoonlight.Host/HostRecoveryAgent.cs"
)
TASK_ACCOUNT_PATH = (
    REPOSITORY_ROOT / "host/VitaMoonlight.Host/ScheduledTaskAccount.cs"
)
BACKEND_LIFECYCLE_PATH = (
    REPOSITORY_ROOT / "host/VitaMoonlight.Host/BackendLifecycleManager.cs"
)
DISPLAY_TOPOLOGY_PATH = (
    REPOSITORY_ROOT / "host/VitaMoonlight.Host/DisplayTopologyService.cs"
)
DISPLAY_WIZARD_PATH = (
    REPOSITORY_ROOT / "host/VitaMoonlight.Host/DisplayWizardAdapter.cs"
)
PROGRAM_PATH = REPOSITORY_ROOT / "host/VitaMoonlight.Host/Program.cs"
LEGACY_HOSTS_PATH = REPOSITORY_ROOT / "host/tests/legacy-upgrade-hosts.json"


class ContractFailure(Exception):
    """Raised when a release-critical upgrade invariant is absent."""


def read(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8-sig")
    except OSError as exc:
        relative = path.relative_to(REPOSITORY_ROOT)
        raise ContractFailure(f"could not read {relative}: {exc}") from exc


def section(source: str, start: str, end: str, description: str) -> str:
    start_index = source.find(start)
    if start_index < 0:
        raise ContractFailure(f"could not find the start of {description}")
    end_index = source.find(end, start_index + len(start))
    if end_index < 0:
        raise ContractFailure(f"could not find the end of {description}")
    return source[start_index:end_index]


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ContractFailure(message)


def require_in_order(source: str, tokens: List[str], description: str) -> None:
    position = -1
    for token in tokens:
        next_position = source.find(token, position + 1)
        if next_position < 0:
            raise ContractFailure(f"{description}: missing {token!r}")
        position = next_position


def load_legacy_hosts() -> list[dict[str, object]]:
    try:
        document = json.loads(read(LEGACY_HOSTS_PATH))
    except json.JSONDecodeError as exc:
        raise ContractFailure(f"legacy host fixture is invalid JSON: {exc}") from exc
    require(
        document.get("schema") == "vita-moonlight/legacy-upgrade-hosts/v1",
        "legacy host fixture has an unsupported schema",
    )
    hosts = document.get("hosts")
    require(isinstance(hosts, list) and bool(hosts), "legacy host fixture is empty")
    return hosts


def check_installer_legacy_surface(installer: str) -> None:
    prepare = section(
        installer,
        "function PrepareToInstall(",
        "procedure DeinitializeSetup;",
        "PrepareToInstall",
    )
    runner = section(
        installer,
        "function RunPreflightHostCommand(",
        "function BackendLifecycleStateExists:",
        "RunPreflightHostCommand",
    )
    begin = section(
        installer,
        "function BeginUpgradeMaintenance(",
        "function EndUpgradeMaintenance:",
        "BeginUpgradeMaintenance",
    )
    rollback = section(
        installer,
        "procedure TryRestoreUpgradeSafeguards;",
        "function PrepareToInstall(",
        "TryRestoreUpgradeSafeguards",
    )

    require(
        "PreflightHostPath" in runner,
        "preflight runner no longer clearly targets the installed host",
    )
    require(
        "WithMaintenanceBypass(Parameters)" in runner,
        "installed-host preflight no longer carries the live maintenance owner",
    )
    commands = re.findall(
        r"RunPreflightHostCommand\(\s*'[^']*',\s*'([^']+)'",
        prepare,
        flags=re.DOTALL,
    )
    require(bool(commands), "PrepareToInstall has no installed-host safeguard commands")
    require(
        len(commands) == prepare.count("RunPreflightHostCommand("),
        "every installed-host preflight call must use a literal command "
        "which the legacy compatibility fixture can audit",
    )

    rollback_commands = re.findall(
        r"WithMaintenanceBypass\('([^']+)'\)",
        rollback,
    )
    require(
        len(rollback_commands) == rollback.count("WithMaintenanceBypass("),
        "every installed-host rollback call must use a literal command "
        "which the legacy compatibility fixture can audit",
    )
    require(
        len(rollback_commands) == rollback.count("InstalledHostPath,"),
        "every installed-host rollback Exec call must pass through the "
        "audited maintenance-bypass command surface",
    )
    backend_probe = "backend status --intent-exit-code"
    common_rollback_commands = [
        command for command in rollback_commands if command != backend_probe
    ]

    legacy_hosts = load_legacy_hosts()
    for host in legacy_hosts:
        version = host.get("productVersion")
        sha256 = host.get("sha256")
        backend_lifecycle_record = host.get("backendLifecycleRecord")
        supported = host.get("preReplacementCommands")
        rollback_supported = host.get("rollbackCommands")
        unsupported = host.get("unsupportedCommands")
        require(
            isinstance(version, str) and bool(version),
            "legacy host fixture has no productVersion",
        )
        require(
            isinstance(sha256, str) and re.fullmatch(r"[0-9a-f]{64}", sha256) is not None,
            f"legacy host {version} has no valid observed SHA-256",
        )
        require(
            isinstance(backend_lifecycle_record, bool),
            f"legacy host {version} has no backendLifecycleRecord classification",
        )
        require(
            isinstance(supported, list) and all(isinstance(item, str) for item in supported),
            f"legacy host {version} has an invalid preReplacementCommands list",
        )
        require(
            isinstance(rollback_supported, list)
            and all(isinstance(item, str) for item in rollback_supported),
            f"legacy host {version} has an invalid rollbackCommands list",
        )
        require(
            isinstance(unsupported, list) and all(isinstance(item, str) for item in unsupported),
            f"legacy host {version} has an invalid unsupportedCommands list",
        )
        unexpected = sorted(set(commands) - set(supported))
        require(
            not unexpected,
            f"PrepareToInstall invokes commands unsupported by legacy host {version}: "
            + ", ".join(unexpected),
        )
        forbidden = sorted(set(commands).intersection(unsupported))
        require(
            not forbidden,
            f"PrepareToInstall invokes known current-only commands on legacy host {version}: "
            + ", ".join(forbidden),
        )
        rollback_unexpected = sorted(
            set(common_rollback_commands) - set(rollback_supported)
        )
        require(
            not rollback_unexpected,
            f"Rollback invokes commands unsupported by legacy host {version}: "
            + ", ".join(rollback_unexpected),
        )
        rollback_forbidden = sorted(
            set(common_rollback_commands).intersection(unsupported)
        )
        require(
            not rollback_forbidden,
            f"Rollback invokes known current-only commands on legacy host {version}: "
            + ", ".join(rollback_forbidden),
        )
        if backend_lifecycle_record:
            require(
                backend_probe in rollback_supported,
                f"legacy host {version} has lifecycle state but cannot report its intent",
            )
        else:
            require(
                backend_probe in unsupported,
                f"legacy host {version} without lifecycle state must classify "
                "the gated backend probe as unsupported",
            )

    require(
        "'uninstall prepare'" not in prepare,
        "PrepareToInstall must not send current-only `uninstall prepare` "
        "to an older installed host",
    )
    require_in_order(
        prepare,
        [
            "BeginUpgradeMaintenance(ErrorText)",
            "QueryMaintenanceSnapshotState(",
            "PreflightHostPath :=",
            "RunPreflightHostCommand(",
        ],
        "upgrade safety gate ordering",
    )
    require_in_order(
        begin,
        [
            "ExtractMaintenanceHelper(ErrorText)",
            "VitaMoonlight.Host.Maintenance.error.txt",
            "VitaMoonlight.Host.Maintenance.exe",
            "maintenance begin --owner-pid ",
            "if ResultCode <> 0 then",
            "ReadMaintenanceHelperError",
            "MaintenanceFenceActive := True",
        ],
        "maintenance helper begin ordering",
    )
    require_in_order(
        rollback,
        [
            "ResultCode := 0",
            "if not BackendLifecycleStateExists then",
            "else if not Exec(",
            "WithMaintenanceBypass('backend status --intent-exit-code')",
        ],
        "legacy rollback capability gate",
    )


def check_installer_failure_ux(installer: str) -> None:
    """Keep expected host failures out of Pascal's runtime-error surface."""

    require(
        "RaiseException(" not in installer,
        "ordinary installer failures must not escape as Inno Runtime error dialogs",
    )
    recorder = section(
        installer,
        "procedure RecordSetupFailure(const ErrorText: String);\nbegin",
        "function PrepareToInstall(",
        "RecordSetupFailure",
    )
    require_in_order(
        recorder,
        [
            "SetupFailureRecorded := True",
            "SetupFailureText :=",
            "ConfigurationDeferredForRestart := True",
            "TryRestoreUpgradeSafeguards",
            "SetupFailurePage.RichEditViewer.Lines.Text := SetupFailureText",
        ],
        "post-copy setup failure recording and safeguard rollback",
    )
    require(
        "No later host-configuration steps were run" in recorder,
        "the controlled failure page must explain that fail-closed sequencing stopped",
    )

    page = section(
        installer,
        "procedure InitializeWizard;",
        "function InitializeSetup: Boolean;",
        "setup failure page",
    )
    require_in_order(
        page,
        [
            "CreateOutputMsgMemoPage(",
            "wpInstalling",
            "Setup could not complete",
            "Vita Moonlight Host stopped safely.",
        ],
        "normal post-copy setup failure page",
    )

    runner = section(
        installer,
        "function RunHostCommand(",
        "function RunRequiredHostCommand(",
        "post-copy host-command runner",
    )
    require(
        runner.count("RecordSetupFailure(") >= 3,
        "host launch failures and detailed/non-detailed nonzero exits must use "
        "the controlled failure page",
    )
    require_in_order(
        runner,
        [
            "LoadStringFromFile(ErrorPath, ErrorDetails)",
            "ErrorText := Trim(ErrorDetails)",
            "RecordSetupFailure(",
            "Result := False",
            "exit",
        ],
        "detailed host-command error capture",
    )

    postinstall = section(
        installer,
        "procedure CurStepChanged(CurStep: TSetupStep);",
        "function ShouldSkipPage(PageID: Integer): Boolean;",
        "post-copy setup orchestration",
    )
    require(
        postinstall.count("RecordSetupFailure(") >= 3,
        "direct readiness, lifecycle, and safeguard failures must use the "
        "controlled failure page",
    )
    direct_failures = re.findall(
        r"RecordSetupFailure\(.*?\);\s*exit;",
        postinstall,
        flags=re.DOTALL,
    )
    require(
        len(direct_failures) == postinstall.count("RecordSetupFailure("),
        "every direct post-copy failure must exit before any later setup step",
    )

    completion = section(
        installer,
        "function ShouldSkipPage(PageID: Integer): Boolean;",
        "procedure CurPageChanged(CurPageID: Integer);",
        "controlled setup completion",
    )
    require_in_order(
        completion,
        [
            "PageID = SetupFailurePage.ID",
            "not SetupFailureRecorded",
            "PageID = wpFinished",
            "SetupFailureRecorded",
            "function GetCustomSetupExitCode: Integer",
            "SetupHostConfigurationFailedExitCode",
        ],
        "failure-page routing and nonzero setup result",
    )
    require(
        "SetupHostConfigurationFailedExitCode = 10" in installer,
        "post-copy setup failures need a stable nonzero product exit code",
    )
    launch_gate = section(
        installer,
        "function CanLaunchControlPanel: Boolean;",
        "function NeedRestart: Boolean;",
        "postinstall control-panel launch gate",
    )
    require(
        "not SetupFailureRecorded" in launch_gate,
        "a failed setup must not offer the successful postinstall launch action",
    )


def check_helper_physical_proof(maintenance: str, uninstall: str) -> None:
    begin = section(
        maintenance,
        "internal static InstallerMaintenanceState Begin(",
        "internal static bool End(",
        "InstallerMaintenanceFence.Begin",
    )
    recovery_call = (
        ".RecoverPhysicalAndDiscardPendingTransactionForInstallerMaintenanceBootstrap("
    )
    require(
        begin.count(recovery_call) == 2,
        "maintenance begin must recover physically in both legacy-unprotected "
        "and current-protected branches",
    )
    require_in_order(
        begin,
        [
            "if (bootstrapFirst)",
            recovery_call,
            "using var backendOperation",
            "if (!bootstrapFirst)",
            recovery_call,
            "var snapshot =",
            "TrustedFileSystem.WriteAllText(BackupFile",
            "TrustedFileSystem.WriteAllText(StateFile",
        ],
        "maintenance physical proof before durable fence publication",
    )

    locked_recovery = section(
        uninstall,
        "RecoverPhysicalAndDiscardPendingTransactionLocked(",
        "internal static SunshineIntegrationCleanupResult CleanupIntegration()",
        "installer maintenance physical recovery",
    )
    require(
        locked_recovery.count("VerifyPhysicalOnlyTopology(topology)") >= 2,
        "installer maintenance recovery must prove physical-only topology "
        "before and after state cleanup",
    )
    require_in_order(
        locked_recovery,
        [
            "topology.RecoverPhysicalDisplays()",
            "topology.DisableManagedVirtualDisplays()",
            "VerifyPhysicalOnlyTopology(topology)",
            "SessionManager.DiscardPendingRecoveryLocked(transaction)",
            "VerifyPhysicalOnlyTopology(topology)",
        ],
        "installer maintenance physical-only recovery",
    )

    physical_verification = section(
        uninstall,
        "internal static void VerifyPhysicalOnlyTopology(",
        "private static T WithSunshineStopped<T>(",
        "physical-only topology verification",
    )
    require_in_order(
        physical_verification,
        [
            "activePhysical.Length == 0",
            "throw new InvalidOperationException(",
            "activeManagedVirtual.Length > 0",
            "throw new InvalidOperationException(",
        ],
        "unsafe display topologies fail closed",
    )


def check_vdd_only_repair_bridge(
    installer: str,
    maintenance: str,
    uninstall: str,
    rescue_agent: str,
    display_topology: str,
    display_wizard: str,
    program: str,
) -> None:
    """An old VDD-only install must be repairable without widening ownership."""

    begin = section(
        maintenance,
        "internal static InstallerMaintenanceState Begin(",
        "internal static bool End(",
        "InstallerMaintenanceFence.Begin",
    )
    require(
        begin.count("ResolveManagedVddBootstrapPermission(") == 2,
        "both protected and legacy installer-maintenance branches must gate "
        "the VDD-only bridge on the saved backend preference",
    )
    permission = section(
        maintenance,
        "private static bool ResolveManagedVddBootstrapPermission(",
        "internal static bool ManagedVddBootstrapAllowedForTest(",
        "managed-VDD bootstrap preference gate",
    )
    require_in_order(
        permission,
        [
            "NormalizeSnapshotForTakeover(existing).BackendWasEnabled",
            "BackendLifecycleManager.ReadPreference()",
            "BackendPreferenceState.Error",
            "throw new InvalidOperationException(",
        ],
        "paused/corrupt backend preference must fail before a VDD restart",
    )

    bridge = section(
        uninstall,
        "RecoverPhysicalAndDiscardPendingTransactionWithManagedVddBootstrapLocked(",
        "private static void RequireExactManagedVddOnlyRecoveryTopology(",
        "managed-VDD-only repair bridge",
    )
    require_in_order(
        bridge,
        [
            "RecoverPhysicalAndDiscardPendingTransactionLocked(",
            "catch (PhysicalDisplayUnavailableException",
            "RequireExactManagedVddOnlyRecoveryTopology(operationName)",
            "DisplayWizardAdapter.RescanDisplayDevicesForRecovery()",
            "TryRecoverPhysicalAfterBootstrapLocked(",
            "if (!allowManagedVddRestart)",
            "RequireExactManagedVddOnlyRecoveryTopology(operationName)",
            "DisplayWizardAdapter.RestartExactManagedVddForRecovery(",
            "TryRecoverPhysicalAfterBootstrapLocked(",
            "could not prove a physical-only display layout",
        ],
        "rescan, exact restart, and final physical proof ordering",
    )
    require(
        "InstallDriver(" not in bridge
        and "UninstallDriver(" not in bridge
        and "SetManagedDriverEnabled(" not in bridge,
        "the repair bridge must not install, remove, enable, or disable a shared VDD",
    )
    topology_gate = section(
        display_topology,
        "internal static DisplayDescriptor? SelectExactManagedVddOnlyRecoveryPath(",
        "internal void SaveRecovery(",
        "exact managed-VDD-only topology selector",
    )
    require_in_order(
        topology_gate,
        [
            "display.IsActive",
            "active.Length == 1",
            "active[0].IsAvailable",
            "IsManagedVirtualDisplay(active[0])",
        ],
        "old broken-install topology must be exact and unambiguous",
    )
    restart = section(
        display_wizard,
        "internal static string RestartExactManagedVddForRecovery(",
        "internal static IReadOnlyList<ManagedVddDeviceStatus>",
        "exact managed-VDD device restart",
    )
    require_in_order(
        restart,
        [
            "SelectExactManagedVddRestartTarget(",
            '"/restart-device"',
            "candidates.Length != 1",
            "No unrelated or ambiguous display device was changed",
        ],
        "exact managed device ownership gate",
    )
    require(
        '"/disable-device"' not in restart
        and '"/remove-device"' not in restart
        and '"/delete-driver"' not in restart,
        "the VDD-only bridge must not disable/remove a shared display or package",
    )

    emergency = section(
        rescue_agent,
        "internal static HostRescueStatus RecoverDisplayAndStreamingHostLocked(",
        "internal static HostRescueStatus RecordUnhandledFailure(",
        "emergency display recovery",
    )
    require_in_order(
        emergency,
        [
            "catch (PhysicalDisplayUnavailableException",
            "WindowsServiceManager.Stop(",
            "sunshineStopped = true",
            "RecoverPhysicalAndDiscardPendingTransactionForEmergencyLocked(",
            "physicalRecoverySucceeded = true",
            "physicalRecoverySucceeded &&",
            "if (restartSunshine && sunshineStopped)",
            "WindowsServiceManager.Start(",
        ],
        "Sunshine stop/restart and emergency bridge semantics",
    )

    restart_catch = section(
        program,
        "catch (HostRestartRequiredException error)",
        "catch (Exception error)",
        "restart-required command reporting",
    )
    require(
        "TryWriteLastCommandError" in restart_catch
        and "TryWriteMaintenanceHelperError" in restart_catch,
        "a bootstrap restart requirement must survive hidden helper execution",
    )
    installer_begin = section(
        installer,
        "function BeginUpgradeMaintenance(",
        "function EndUpgradeMaintenance:",
        "installer bootstrap restart handling",
    )
    require_in_order(
        installer_begin,
        [
            "if ResultCode = 4 then",
            "RestartRequiredByPrerequisite := True",
            "ReadMaintenanceHelperError",
            "No application files or recovery safeguards were replaced",
            "if ResultCode <> 0 then",
        ],
        "installer restart-required fail-closed path",
    )


def check_interactive_task_account_safety(
    maintenance: str,
    uninstall: str,
    recovery_task: str,
    rescue_agent: str,
    task_account: str,
    backend_lifecycle: str,
) -> None:
    begin = section(
        maintenance,
        "internal static InstallerMaintenanceState Begin(",
        "internal static bool End(",
        "InstallerMaintenanceFence.Begin",
    )
    require_in_order(
        begin,
        [
            "ScheduledTaskAccount.RequireCurrentInteractiveUser(",
            "RequireLiveProcessStart(ownerProcessId)",
            ".RecoverPhysicalAndDiscardPendingTransactionForInstallerMaintenanceBootstrap(",
        ],
        "different-account UAC must fail before installer mutation",
    )
    snapshot = section(
        maintenance,
        "private static MaintenanceSafeguardSnapshot CaptureCurrentSnapshot()",
        "private static MaintenanceSafeguardSnapshot NormalizeSnapshotForTakeover(",
        "installer task snapshot",
    )
    require(
        snapshot.count("ExactScheduledTaskManager.RequireOwnedInteractiveTask(") == 2,
        "installer maintenance must reject same-name foreign tasks before an "
        "older installed host can remove them",
    )
    require(
        "WTSQuerySessionInformation" in task_account
        and "WindowsIdentity.GetCurrent()" in task_account
        and "different administrator password is intentionally rejected" in task_account,
        "scheduled-task account guard must compare the elevated identity with "
        "the interactive session and explain unsupported different-account UAC",
    )
    require(
        "ScheduledTaskAccount" not in uninstall,
        "uninstall must remain available to another elevated Administrator; "
        "only task installation/repair is account-bound",
    )
    uninstall_prepare = section(
        uninstall,
        "internal static UninstallPreparationResult Prepare(",
        "internal static UninstallPreparationResult\n        RecoverPhysicalAndDiscardPendingTransaction()",
        "UninstallManager.Prepare",
    )
    require_in_order(
        uninstall_prepare,
        [
            "BackendLifecycleStateStore.AcquireLock()",
            "RequireOwnedFinalizationPreflight()",
            "BackendLifecycleStateStore.BeginUninstallLocked(",
            "RecoverPhysicalAndDiscardPendingTransaction()",
            "operationLock.Dispose()",
        ],
        "uninstall must preflight exact owned cleanup before its durable mutation",
    )
    require(
        uninstall_prepare.count(
            "ExactScheduledTaskManager.RequireOwnedInteractiveTask("
        ) == 2
        and "SunshineConfigurator.RequireManagedIntegrationCleanupReady()"
        in uninstall_prepare,
        "uninstall preflight must verify both task actions and streaming-host cleanup",
    )
    enable = section(
        backend_lifecycle,
        "internal static BackendLifecycleReport EnableLocked(",
        "/// <summary>",
        "BackendLifecycleManager.EnableLocked",
    )
    require_in_order(
        enable,
        [
            "ScheduledTaskAccount.RequireCurrentInteractiveUser(",
            "BackendLifecycleStateStore.LoadForLifecycleAction(",
            "BackendLifecycleStateStore.Save(transition)",
            "RecoveryTaskManager.Install(",
        ],
        "enable must reject different-account UAC before lifecycle or task mutation",
    )

    for source, description in (
        (recovery_task, "display-recovery task"),
        (rescue_agent, "stream-rescue task"),
    ):
        install = section(
            source,
            "internal static void Install(string executablePath)",
            "internal static void Uninstall()",
            f"{description} install",
        )
        uninstall_task = section(
            source,
            "internal static void Uninstall()",
            "private static int Run" if description == "display-recovery task" else "internal static HostRescueStatus? ReadLastStatus()",
            f"{description} uninstall",
        )
        require_in_order(
            install,
            [
                "ScheduledTaskAccount.RequireCurrentInteractiveUser(",
                "if (existing.State == ExactScheduledTaskState.Present)",
                "ExactScheduledTaskManager.RequireOwnedInteractiveTask(",
                "requireInteractiveHighest: false",
                '"/IT"',
                '"/RL", "HIGHEST"',
                "ExactScheduledTaskManager.RequireOwnedInteractiveTask(",
            ],
            f"{description} interactive/highest creation",
        )
        require_in_order(
            uninstall_task,
            [
                "ExactScheduledTaskManager.RequireOwnedInteractiveTask(",
                "ExactScheduledTaskManager.DeleteExact(",
            ],
            f"{description} exact-action deletion",
        )


def check_release_scope_and_rescue_surface(installer: str) -> None:
    require(
        'Name: "host\\sunshine"' in installer
        and 'Name: "host\\sunshine\\virtualdriver"' not in installer
        and 'Name: "host\\apollo"' not in installer,
        "the public installer must expose one supported Sunshine + required VDD path",
    )
    require(
        "deferred-setup save --host sunshine --virtual-driver true" in installer,
        "paused Sunshine setup must retain the mandatory VDD plan",
    )
    program = read(REPOSITORY_ROOT / "host/VitaMoonlight.Host/Program.cs")
    require_in_order(
        program,
        [
            "TryWriteLastCommandError(errorDetails)",
            "TryWriteMaintenanceHelperError(errorDetails)",
            "private static void TryWriteMaintenanceHelperError(",
            '"VitaMoonlight.Host.Maintenance.exe"',
            "Path.GetRelativePath(",
            '"VitaMoonlight.Host.Maintenance.error.txt"',
        ],
        "setup-private helper errors must explain pre-install account rejection",
    )

    forbidden = (
        "Close Windows game",
        "close-game",
        "close-foreground",
        "CloseForeground",
        "VkF12",
        "UI_ACTION_END_WINDOWS",
    )
    roots = (
        REPOSITORY_ROOT / "README.md",
        REPOSITORY_ROOT / "host",
        REPOSITORY_ROOT / "docs",
        REPOSITORY_ROOT / "protocol",
        REPOSITORY_ROOT / "src",
    )
    candidates: list[Path] = []
    for root in roots:
        if root.is_file():
            candidates.append(root)
        else:
            candidates.extend(
                path
                for path in root.rglob("*")
                if path.is_file()
                and path.suffix.lower()
                in {".c", ".h", ".cs", ".iss", ".json", ".md", ".py"}
                and "obj" not in path.parts
                and "bin" not in path.parts
        )
    for path in candidates:
        source = read(path)
        for token in forbidden:
            require(
                token.lower() not in source.lower(),
                f"unsafe foreground-close/F12 rescue surface remains in "
                f"{path.relative_to(REPOSITORY_ROOT)}: {token}",
            )


def main() -> int:
    try:
        installer = read(INSTALLER_PATH)
        check_installer_legacy_surface(installer)
        check_installer_failure_ux(installer)
        check_helper_physical_proof(
            read(MAINTENANCE_PATH),
            read(UNINSTALL_PATH),
        )
        check_vdd_only_repair_bridge(
            installer,
            read(MAINTENANCE_PATH),
            read(UNINSTALL_PATH),
            read(RESCUE_AGENT_PATH),
            read(DISPLAY_TOPOLOGY_PATH),
            read(DISPLAY_WIZARD_PATH),
            read(PROGRAM_PATH),
        )
        check_interactive_task_account_safety(
            read(MAINTENANCE_PATH),
            read(UNINSTALL_PATH),
            read(RECOVERY_TASK_PATH),
            read(RESCUE_AGENT_PATH),
            read(TASK_ACCOUNT_PATH),
            read(BACKEND_LIFECYCLE_PATH),
        )
        check_release_scope_and_rescue_surface(installer)
    except ContractFailure as exc:
        print(f"Windows upgrade contract check failed: {exc}", file=sys.stderr)
        return 1

    print(
        "Windows upgrade contract check passed: legacy installed-host commands "
        "are compatible, expected setup failures use a normal failure page and "
        "nonzero result, the embedded helper proves physical-only safety, "
        "interactive tasks cannot bind to different-account UAC, uninstall "
        "remains available, and no unsafe foreground-close rescue exists."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
