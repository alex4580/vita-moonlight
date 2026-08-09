#!/usr/bin/env python3
"""Statically verify the safe Windows in-place-upgrade compatibility contract.

The installer must repair older installations before it can replace their
host executable.  The old executable is deliberately treated as inert data:
all protected pre-copy task removal and cancel rollback must run through the
current host embedded in setup.  This catches upgrade bootstrap failures which
the candidate cannot fix by changing only the eventually installed payload.

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
MACHINE_STATE_PATH = (
    REPOSITORY_ROOT / "host/VitaMoonlight.Host/MachineStateSecurity.cs"
)
INSTALL_RESIDUE_PATH = (
    REPOSITORY_ROOT / "host/VitaMoonlight.Host/InstallResidueCleanup.cs"
)
VDD_NORMALIZER_PATH = (
    REPOSITORY_ROOT / "host/VitaMoonlight.Host/VddConfigurationNormalizer.cs"
)
MANAGED_VDD_RUNTIME_PATH = (
    REPOSITORY_ROOT / "host/VitaMoonlight.Host/ManagedVirtualDisplayRuntime.cs"
)
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


def check_installer_precopy_helper_surface(
    installer: str,
    program: str,
    maintenance: str,
    rescue_agent: str,
    recovery_task: str,
) -> None:
    prepare = section(
        installer,
        "function PrepareToInstall(",
        "procedure DeinitializeSetup;",
        "PrepareToInstall",
    )
    runner = section(
        installer,
        "function RunMaintenanceSafeguardCommand(",
        "procedure TryRestoreUpgradeSafeguards;",
        "RunMaintenanceSafeguardCommand",
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
    deinitialize = section(
        installer,
        "procedure DeinitializeSetup;",
        "function DriverReadyForConfiguration:",
        "installer cancel/failure cleanup",
    )

    # Keep the observed legacy fixture as release evidence, but never delegate
    # a protected mutation to those binaries.  In particular, <=0.14.8 can
    # throw FileNotFoundException while removing an absent firewall rule.
    legacy_hosts = load_legacy_hosts()
    for host in legacy_hosts:
        version = host.get("productVersion")
        sha256 = host.get("sha256")
        require(
            isinstance(version, str) and bool(version),
            "legacy host fixture has no productVersion",
        )
        require(
            isinstance(sha256, str) and re.fullmatch(r"[0-9a-f]{64}", sha256) is not None,
            f"legacy host {version} has no valid observed SHA-256",
        )
        require(
            isinstance(host.get("backendLifecycleRecord"), bool)
            and isinstance(host.get("preReplacementCommands"), list)
            and isinstance(host.get("rollbackCommands"), list)
            and isinstance(host.get("unsupportedCommands"), list),
            f"legacy host {version} has an incomplete observed command fixture",
        )

    require(
        "RunPreflightHostCommand(" not in installer,
        "setup can still delegate a protected pre-copy mutation to the old installed host",
    )
    require(
        "PreflightHostPath," not in prepare and
        "InstalledHostPath," not in rollback and
        "WithMaintenanceBypass(" not in rollback and
        "Exec(" not in prepare and
        "Exec(" not in rollback,
        "pre-copy or cancel rollback can still execute the old installed host",
    )
    require(
        "VitaMoonlight.Host.Maintenance.exe" in runner and
        "'maintenance ' + Action + ' --owner-pid '" in runner and
        "ReadMaintenanceHelperError" in runner,
        "the protected safeguard runner is not bound to the current embedded helper, live owner, and helper error channel",
    )
    require(
        prepare.count("'suspend-safeguards'") >= 1 and
        "RunMaintenanceSafeguardCommand(" in prepare,
        "PrepareToInstall does not suspend exact safeguards through the current embedded helper",
    )
    require(
        "'restore-safeguards'" in rollback and
        "RunMaintenanceSafeguardCommand(" in rollback,
        "cancel/failure rollback does not restore safeguards through the current embedded helper",
    )
    require(
        "'agent uninstall'" not in prepare and
        "'recovery uninstall'" not in prepare and
        "'agent install'" not in rollback and
        "'recovery install'" not in rollback,
        "installer still exposes old-host task mutations instead of one current-helper transaction",
    )
    require_in_order(
        prepare,
        [
            "BeginUpgradeMaintenance(ErrorText)",
            "QueryMaintenanceSnapshotState(",
            "ConfirmManagedVddAdoption(ErrorText)",
            "RunMaintenanceSafeguardCommand(",
            "'suspend-safeguards'",
        ],
        "upgrade safety gate ordering",
    )
    require_in_order(
        begin,
        [
            "ExtractMaintenanceHelper(ErrorText)",
            "VitaMoonlight.Host.Maintenance.error.txt",
            "maintenance begin --owner-pid ",
            "VitaMoonlight.Host.Maintenance.exe",
            "if ResultCode <> 0 then",
            "ReadMaintenanceHelperError",
            "MaintenanceFenceActive := True",
        ],
        "maintenance helper begin ordering",
    )
    maintenance_command = section(
        program,
        "private static int MaintenanceCommand(",
        "private static int BackendCommand(",
        "maintenance command surface",
    )
    require(
        'case "suspend-safeguards":' in maintenance_command and
        'case "restore-safeguards":' in maintenance_command,
        "the embedded host does not expose both exact safeguard maintenance actions",
    )
    require(
        '"--installed-executable"' not in maintenance_command and
        '"--installed-executable"' not in maintenance and
        "InstallationTrust.ExpectedExecutablePath" in maintenance,
        "safeguard maintenance accepts a caller-selected target instead of the exact Program Files path",
    )
    require_in_order(
        maintenance_command,
        [
            'case "suspend-safeguards":',
            "EnsureMaintenanceAdministrator(",
            '"--owner-pid"',
            "InstallerMaintenanceFence.SuspendSafeguardsForOwner(",
            'case "restore-safeguards":',
            "EnsureMaintenanceAdministrator(",
            '"--owner-pid"',
            "InstallerMaintenanceFence.RestoreSafeguardsForOwner(",
        ],
        "maintenance safeguard command authorization",
    )
    suspend = section(
        maintenance,
        "internal static void SuspendSafeguardsForOwner(",
        "internal static void RestoreSafeguardsForOwner(",
        "fence-owned safeguard suspension",
    )
    restore = section(
        maintenance,
        "internal static void RestoreSafeguardsForOwner(",
        "internal static (bool RescueAgent, bool RecoveryTask)",
        "fence-owned safeguard restoration",
    )
    for operation_name, operation in (
        ("suspend", suspend),
        ("restore", restore),
    ):
        require_in_order(
            operation,
            [
                "RequireBootstrapHelper(ownerProcessId)",
                "RequireLiveProcessStart(ownerProcessId)",
                "AcquireCommandGate()",
                "ownsFence: true",
                "RequireOwnedSnapshot(",
                "InstallationTrust.ExpectedExecutablePath",
            ],
            f"fence-owned safeguard {operation_name}",
        )
    require(
        "BackendLifecycleManager.ReadPreference()" in restore and
        "BackendPreferenceState.Error" in restore and
        restore.find("BackendPreferenceState.Error") <
        restore.find("RestoreSafeguardsCore("),
        "safeguard restoration does not fail closed on an unreadable, deferred, or uninstalling backend preference",
    )
    suspend_core = section(
        maintenance,
        "private static void SuspendSafeguardsCore(",
        "private static void RestoreSafeguardsCore(",
        "exact safeguard suspension core",
    )
    restore_core = section(
        maintenance,
        "private static void RestoreSafeguardsCore(",
        "private static (bool RescueAgent, bool RecoveryTask)\n        RequireSafeguardMutationPreflight(",
        "exact safeguard restoration core",
    )
    preflight = section(
        maintenance,
        "private static (bool RescueAgent, bool RecoveryTask)\n        RequireSafeguardMutationPreflight(",
        "private static void RequireSafeguardState(",
        "safeguard mutation preflight",
    )
    require_in_order(
        preflight,
        [
            "HostRecoveryAgentManager.GetInstallationState()",
            "RecoveryTaskManager.GetInstallationState()",
            "ExactScheduledTaskManager.RequireKnown(",
            "ExactScheduledTaskManager.RequireKnown(",
            "ExactScheduledTaskManager.RequireOwnedInteractiveTask(",
            "ExactScheduledTaskManager.RequireOwnedInteractiveTask(",
        ],
        "prevalidate both exact task definitions",
    )
    require(
        preflight.count("requireCurrentUser: true") >= 2,
        "safeguard preflight can trust a same-name task owned by another interactive account",
    )
    require_in_order(
        suspend_core,
        [
            "RequireSafeguardMutationPreflight(",
            "ShouldReconcileRescueForTest(",
            "present.RescueAgent",
            "IsExactInstalledExecutableAvailable(executablePath)",
            "if (reconcileRescue)",
            "ManagedStreamBridgeFirewall.RequireAbsentOrOwned(",
            "HostRecoveryAgentManager.StopForMaintenance(",
            "if (!deleteDefinitions)",
            "RecoveryTaskManager.TaskName",
            "if (reconcileRescue)",
            "HostRecoveryAgentManager.Uninstall(",
            "executablePath",
            "requireCurrentUser: true",
            "RecoveryTaskManager.Uninstall(",
            "RequireSafeguardState(",
        ],
        "prevalidated all-or-fail safeguard suspension",
    )
    require(
        "IsExactInstalledExecutableAvailable(executablePath)" in suspend and
        "SuspendSafeguardsCore(executablePath, deleteDefinitions)" in suspend and
        suspend_core.find("StopForMaintenance(") <
        suspend_core.find("if (!deleteDefinitions)"),
        "a missing old executable can block stopping the exact safeguards before setup repairs the payload",
    )
    require(
        suspend_core.find("RequireSafeguardMutationPreflight(") <
        suspend_core.find("RequireAbsentOrOwned(") and
        suspend_core.find("RequireAbsentOrOwned(") <
        suspend_core.find("HostRecoveryAgentManager.Uninstall(") and
        suspend_core.find("RequireSafeguardMutationPreflight(") <
        suspend_core.find("RecoveryTaskManager.Uninstall("),
        "safeguard suspension can mutate an exact task before its definitions, and any affected firewall rule, are proven owned",
    )
    require_in_order(
        restore_core,
        [
            "RequireSafeguardMutationPreflight(",
            "IsExactInstalledExecutableAvailable(executablePath)",
            "ShouldReconcileRescueForTest(",
            "present.RescueAgent",
            "executableAvailable",
            "if (reconcileRescue)",
            "ManagedStreamBridgeFirewall.RequireAbsentOrOwned(",
            "RecoveryTaskManager.Install(",
            "HostRecoveryAgentManager.Install(",
            "if (reconcileRescue)",
            "HostRecoveryAgentManager.Uninstall(",
            "executablePath",
            "requireCurrentUser: true",
            "RequireSafeguardState(",
        ],
        "cancel/takeover safeguard restoration",
    )
    reconcile_policy = section(
        maintenance,
        "internal static bool ShouldReconcileRescueForTest(",
        "private static bool IsExactInstalledExecutableAvailable(",
        "stale rescue/firewall reconciliation policy",
    )
    require(
        "rescueTaskPresent || installedExecutableAvailable" in reconcile_policy,
        "rescue cleanup can skip a stale firewall rule when the exact installed host remains, or mutate a genuine no-task/no-host clean install",
    )
    require(
        restore_core.find("RequireAbsentOrOwned(") <
        restore_core.find("RecoveryTaskManager.Install(") and
        restore_core.find("RequireAbsentOrOwned(") <
        restore_core.find("HostRecoveryAgentManager.Uninstall("),
        "restore can mutate a safeguard before preflighting the stale rescue firewall rule",
    )
    require(
        "internal static void Uninstall(\n        string executablePath" in rescue_agent and
        "RemoveOwned(executablePath)" in rescue_agent and
        "internal static void Uninstall(\n        string executablePath" in recovery_task,
        "current-helper suspension is not path-bound through both exact task managers and firewall cleanup",
    )
    rescue_install = section(
        rescue_agent,
        "internal static void Install(string executablePath)",
        "internal static void Uninstall()",
        "rescue task installation",
    )
    recovery_install = section(
        recovery_task,
        "internal static void Install(string executablePath)",
        "internal static void Uninstall()",
        "recovery task installation",
    )
    require(
        rescue_install.count("requireCurrentUser: true") >= 2 and
        recovery_install.count("requireCurrentUser: true") >= 2,
        "task repair does not verify the current interactive principal both before replacement and after creation",
    )
    require(
        suspend_core.count("requireCurrentUser: true") >= 2 and
        restore_core.count("requireCurrentUser: true") >= 2,
        "fence-owned task removal does not request a current-principal race recheck from both managers",
    )
    running = section(
        rescue_agent,
        "internal static bool IsRunning(string executablePath)",
        "internal static IReadOnlyList<HostModeHotkeyStatus>",
        "explicit-path rescue readiness",
    )
    require_in_order(
        running,
        [
            "IsExactProcessPresent(executablePath)",
            "executablePath",
        ],
        "explicit-path rescue process identity",
    )
    process_identity = section(
        rescue_agent,
        "private static bool IsExactProcessPresent(",
        "private static void RequireExpectedAgentProcess(\n        Process process",
        "exact rescue process identity",
    )
    require(
        "FindWindow(" in process_identity and
        "GetWindowThreadProcessId(" in process_identity and
        "RequireExpectedAgentProcess(" in process_identity,
        "rescue readiness does not bind its signaling process to the exact installed executable",
    )
    require(
        "Install(executablePath)" in restore_core and
        "Uninstall(\n                executablePath" in suspend_core and
        "IsRunning(executablePath)" in maintenance,
        "embedded safeguard maintenance can substitute its temporary helper path for the installed task target",
    )
    require(
        "RequireControllerProcess();" in maintenance and
        "RequireLiveProcessStart(ownerProcessId)" in maintenance and
        "RequireOwnedSnapshot(" in maintenance,
        "maintenance actions are not protected by the exact live-owner fence",
    )
    end_verification = section(
        maintenance,
        "private static void VerifySafeToEnd(",
        "internal static bool CanEndForTest(",
        "maintenance fence handoff verification",
    )
    require(
        end_verification.count("RequireOwnedInteractiveTask(") >= 2 and
        "HostRecoveryAgentManager.IsRunning(" in end_verification and
        "InstallationTrust.ExpectedExecutablePath" in end_verification,
        "maintenance can end without exact task ownership and explicit installed-path rescue readiness",
    )
    require_in_order(
        deinitialize,
        [
            "UpgradeAgentWasStopped or UpgradeRecoveryTaskWasRemoved",
            "EndUpgradeMaintenance then",
            "if MaintenanceFenceActive and not UpgradeSafeguardsRestored then",
            "TryRestoreUpgradeSafeguards",
        ],
        "cancel must let the current helper prove or restore the durable task obligations before ending the fence",
    )
    require_in_order(
        rollback,
        [
            "RunMaintenanceSafeguardCommand(",
            "'restore-safeguards'",
            "UpgradeSafeguardsRestored := True",
        ],
        "cancel rollback current-helper invocation",
    )
    require(
        "if not UpgradeBackendWasEnabled then" not in rollback and
        "if not (UpgradeAgentWasStopped or UpgradeRecoveryTaskWasRemoved) then" not in rollback,
        "cancel rollback can skip current-helper reconciliation for a paused or partially suspended upgrade",
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


def check_helper_physical_proof(
    maintenance: str,
    uninstall: str,
    managed_vdd_runtime: str,
) -> None:
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
    require_in_order(
        locked_recovery,
        [
            "ManagedVirtualDisplayRuntime.ReconcileIdleLocked(",
            "SessionManager.DiscardPendingRecoveryLocked(transaction)",
            "VerifyPhysicalOnlyTopology(topology)",
        ],
        "installer maintenance physical-only recovery",
    )
    idle_recovery = section(
        managed_vdd_runtime,
        "internal static ManagedVirtualDisplayIdleResult ReconcileIdleLocked(",
        "internal static ManagedVirtualDisplayIdleResult\n        ReconcileRestoredPhysicalBaselineLocked(",
        "managed VDD idle recovery",
    )
    require(
        idle_recovery.count(
            "UninstallManager.VerifyPhysicalOnlyTopology(topology)"
        ) >= 2,
        "managed VDD idle recovery must prove physical-only topology before "
        "and after PnP shutdown",
    )
    require_in_order(
        idle_recovery,
        [
            "topology.RecoverPhysicalDisplays()",
            "topology.DisableManagedVirtualDisplays()",
            "UninstallManager.VerifyPhysicalOnlyTopology(topology)",
            "DisplayWizardAdapter.SetManagedDriverEnabled(",
            "enabled: false",
            "physicalDisplays = topology.RecoverPhysicalDisplays()",
            "topology.DisableManagedVirtualDisplays()",
            "UninstallManager.VerifyPhysicalOnlyTopology(topology)",
        ],
        "physical-only, PnP-disabled idle invariant",
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
            "IsExactVitaVirtualDisplay(active[0])",
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
            "RequireOwnedEnabledRecoveryTargetLocked(transaction)",
            '"/restart-device"',
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
            "if (restartSunshine &&",
            "sunshineStopped &&",
            "idleStateVerifiedForSunshineRestart)",
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
        "private static void SuspendSafeguardsCore(",
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
        "internal static UninstallPreparationResult\n        RecoverPhysicalAndDiscardPendingTransaction(",
        "UninstallManager.Prepare",
    )
    require_in_order(
        uninstall_prepare,
        [
            "BackendLifecycleStateStore.AcquireLock()",
            "RequireOwnedFinalizationPreflight()",
            "BackendLifecycleStateStore.BeginUninstallLocked(",
            "RecoverPhysicalAndDiscardPendingTransaction(",
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
    require(
        "--adoption-owner-pid " in installer
        and "plan.ExistingDeviceAdoptionInstanceId" in program
        and "RequireStagedVddAdoptionCandidateForOwner" in program,
        "a paused repair must retain the exact approved existing-device identity for its protected deferred driver transaction",
    )
    paused_setup = section(
        installer,
        "if BackendIntent = 5 then",
        "'Installing or repairing ViGEmBus',",
        "paused post-copy setup",
    )
    require(
        "deferred-setup save --host sunshine --virtual-driver true"
        in paused_setup
        and "if AdoptExistingVddApproved then" in paused_setup
        and "driver adopt-idle --adoption-owner-pid " in paused_setup
        and "'backend disable'" not in paused_setup,
        "a paused upgrade must acquire and disable only its exact approved candidate before deferring driver repair, without rerunning a broad legacy device-list mutation",
    )
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


def check_idle_ownership_release_contract(
    installer: str,
    maintenance: str,
    uninstall: str,
    managed_vdd_runtime: str,
    display_topology: str,
    display_wizard: str,
    program: str,
) -> None:
    """Keep idle, upgrade, and uninstall bound to one exact VDD instance."""

    idle = section(
        managed_vdd_runtime,
        "internal static ManagedVirtualDisplayIdleResult ReconcileIdleLocked(",
        "internal static ManagedVirtualDisplayIdleResult\n        ReconcileRestoredPhysicalBaselineLocked(",
        "exact managed-VDD idle reconciliation",
    )
    require_in_order(
        idle,
        [
            "RequireOwnedPresentDevicesLocked(",
            "if (presentInstanceIds.Length == 0)",
            "TryCaptureExactPhysicalOnlySnapshot(",
            "topology.RecoverPhysicalDisplays()",
            "DisplayWizardAdapter.SetManagedDriverEnabled(",
            "enabled: false",
        ],
        "idle must resolve exact authority before topology or PnP mutation",
    )

    capture = section(
        display_topology,
        "internal DisplayRecoveryRecord CaptureRecovery(",
        "internal bool DisableManagedVirtualDisplays()",
        "strict display recovery capture",
    )
    require(
        "TryCaptureExactPhysicalOnlySnapshot(out var exact)" in capture
        and "exact.Configuration" in capture,
        "display recovery must reject incomplete/unnamed active paths",
    )

    require_in_order(
        display_topology,
        [
            "SelectUniqueExactVitaVirtualDisplayForMutation(",
            "exact.Length != 1",
            "!IsExactVitaVirtualDisplay(display)",
            "IsManagedVirtualDisplay(display)",
        ],
        "Vita display mutations must fail closed on an absent, duplicate, or competing managed target",
    )
    require(
        '@"\\\\?\\DISPLAY#MTT1337#"' in display_topology
        and "display.DevicePath.StartsWith(" in display_topology,
        "Vita display mutation authority must come from the exact MTT1337 monitor path",
    )

    safe_end = section(
        maintenance,
        "private static void VerifySafeToEnd(",
        "internal static bool CanEndForTest(",
        "installer maintenance final display proof",
    )
    require_in_order(
        safe_end,
        [
            "DisplayTransactionLock.Acquire()",
            "File.Exists(HostStatePaths.RecoveryFile)",
            "DisplaySuspendIntentStore.Inspect()",
            "TryCaptureExactPhysicalOnlySnapshot(",
            "RequireOwnedPresentDevicesLocked(",
            "ownedDevices.Any(device => device.Enabled)",
        ],
        "maintenance must prove exact PnP-disabled idle before deleting its fence",
    )

    owned_files = re.search(
        r"CurrentRootStateFiles\s*=\s*\[(?P<body>.*?)\];",
        uninstall,
        re.DOTALL,
    )
    require(owned_files is not None, "uninstall owned-state allowlist is missing")
    require(
        "managed-vdd-ownership.json" not in owned_files.group("body")
        and 'Type: files; Name: "{app}\\state\\managed-vdd-ownership.json"'
        in installer,
        "exact VDD authority must survive host finalization and be removed only by the post-commit installer pass",
    )

    require(
        "--prepare-vdd-ownership" not in installer
        and "maintenance begin --owner-pid " in installer
        and "maintenance vdd-adoption-required --owner-pid " in installer
        and "HasCommandLineSwitch('ADOPTEXISTINGVDD')" in installer
        and "if AdoptExistingVddApproved then" in installer
        and "DriverInstallParameters := 'driver install'" in installer
        and "DriverInstallParameters +" in installer
        and "' --adoption-owner-pid '" in installer
        and "driver install --adopt-existing-vdd" not in installer
        and 'case "vdd-adoption-required":' in program
        and "StageVddAdoptionCandidateForOwner(" in program
        and "ExpectedExistingDeviceInstanceId" in read(
            REPOSITORY_ROOT /
            "host/VitaMoonlight.Host/ManagedVddOwnershipJournal.cs"
        ),
        "existing MTT adoption must bind guided or explicit unattended consent to one protected instance identity through the post-copy driver transaction",
    )
    require(
        "StagedVddAdoptionInstanceId" in maintenance
        and "private const int CurrentFormatVersion = 2;" in maintenance
        and "state.Revision == 0" in maintenance
        and "Revision = checked(state.Revision + 1)" in maintenance
        and "primary.Revision >= backup.Revision" in maintenance
        and "primary.OwnerProcessId != backup.OwnerProcessId" in maintenance,
        "the protected staged candidate must retain the v2 wire format read by <=0.14.8, while its additive revision survives a torn backup-first update without accepting another owner",
    )
    ownership = read(
        REPOSITORY_ROOT /
        "host/VitaMoonlight.Host/ManagedVddOwnershipJournal.cs"
    )
    ordinary_install = section(
        ownership,
        "internal static ManagedVddInstallPlan PrepareInstallLocked(",
        "/// <summary>",
        "ordinary post-copy managed-VDD install",
    )
    require(
        "HasExactLegacyVitaOwnershipEvidence(" not in ordinary_install
        and "ExactLegacyVitaOwnershipEvidence: false" in ordinary_install,
        "ordinary post-copy install must never manufacture AppCreated authority from the newly copied executable; exact legacy migration belongs only to pre-copy maintenance",
    )
    installer_bootstrap = section(
        uninstall,
        "RecoverPhysicalAndDiscardPendingTransactionForInstallerMaintenanceBootstrap(",
        "/// <summary>",
        "installer maintenance physical recovery",
    )
    post_copy_recovery = section(
        uninstall,
        "RecoverPhysicalAndDiscardPendingTransactionForRecoveryUpgradeOnly()",
        "RecoverPhysicalAndDiscardPendingTransactionForInstallerMaintenanceBootstrap(",
        "post-copy installer recovery",
    )
    require(
        "MigrateLegacyOwnershipIfProvenLocked(" in installer_bootstrap
        and "MigrateLegacyOwnershipIfProvenLocked(" not in post_copy_recovery
        and "MigrateLegacyOwnershipIfProvenLocked(" not in section(
            uninstall,
            "RecoverPhysicalAndDiscardPendingTransaction()",
            "RecoverPhysicalAndDiscardPendingTransactionForRecoveryUpgradeOnly()",
            "ordinary uninstall recovery",
        ),
        "pre-copy maintenance may migrate only already-proven legacy ownership, while post-copy recovery and uninstall must not manufacture evidence from the newly copied executable",
    )

    driver_uninstall = section(
        display_wizard,
        "internal bool UninstallDriver(",
        "internal bool EnsureVitaCompatibilityModes(",
        "exact VDD uninstall",
    )
    require_in_order(
        driver_uninstall,
        [
            "PrepareReleaseLocked(",
            "WindowsServiceManager.Stop(serviceName, \"Sunshine\")",
            "topology.RecoverPhysicalDisplays()",
            "CompleteReleaseLocked(",
        ],
        "uninstall must stop Sunshine and establish physical topology before releasing exact authority",
    )
    require(
        "if (!File.Exists(ManagedVddOwnershipJournal.JournalFile))"
        in driver_uninstall
        and "The unproven device and shared driver package were left unchanged."
        in driver_uninstall
        and "ManagedVddReleaseAction.RestoreAdoptedInstance"
        in driver_uninstall
        and "release.DesiredEnabled!.Value" in driver_uninstall
        and "restoreManagedVdd: !HasFlag(args, \"--vdd-removed\")"
        in program
        and "if (restoreManagedVdd)" in uninstall
        and "ShouldAttemptManagedVddReleaseForTest(" in uninstall
        and "restoreManagedVdd && ownershipJournalExists" in uninstall
        and "Uninstall did not locate or run driver tools" in uninstall
        and ".UninstallDriver(transaction)" in uninstall,
        "default uninstall must restore an adopted device's exact baseline while preserving a no-journal foreign device and shared package even when the bundled driver tools are missing",
    )

    readiness = section(
        program,
        "private static bool IsVirtualDisplayReady(",
        "private static int SessionCommand(",
        "virtual-display readiness",
    )
    require(
        "RequireOwnedPresentDevices(required: true)" in readiness,
        "readiness must reject a sole unowned hardware-ID match",
    )


def check_versioned_install_cleanup_contract(
    installer: str,
    machine_state: str,
    residue: str,
    display_wizard: str,
    normalizer: str,
    uninstall: str,
) -> None:
    post_install = section(
        installer,
        "procedure CurStepChanged(CurStep: TSetupStep);",
        "procedure CurPageChanged(CurPageID: Integer);",
        "post-copy setup",
    )
    require_in_order(
        post_install,
        [
            "session recover-upgrade",
            "state secure",
        ],
        "protected post-copy cleanup initialization",
    )
    require(
        machine_state.count(
            "InstallResidueCleanup.RunForInstalledPayloadIfNeeded();"
        ) >= 2,
        "normal state security and legacy migration must both run versioned cleanup",
    )
    require_in_order(
        display_wizard,
        [
            "AddVitaCompatibilityModesToConfiguration(string configuration)",
            "VddConfigurationNormalizer.NormalizeForVitaRuntime(updated)",
            "VddConfigurationNormalizer.IsNormalizedForVitaRuntime(configuration)",
        ],
        "VDD repair normalization and readiness",
    )
    require(
        'SetExactScalar(monitors, "count", "1")' in normalizer
        and 'SetExactScalar(options, "logging", "false")' in normalizer
        and 'SetExactScalar(options, "debuglogging", "false")' in normalizer,
        "VDD repair must enforce one monitor with normal/debug logging disabled",
    )

    expected_obsolete = {
        "COMPATIBILITY.md",
        "END_TO_END_TEST.md",
        "FINAL_RELEASE_CHECKLIST.md",
        "THIRD_PARTY_NOTICES.md",
        "VITA_SETTINGS_GUIDE.md",
    }
    obsolete_match = re.search(
        r"ObsoleteRootPayloadFilesV1\s*=\s*\[(?P<body>.*?)\];",
        residue,
        re.DOTALL,
    )
    require(obsolete_match is not None, "cleanup v1 root allowlist is missing")
    actual_obsolete = set(re.findall(r'"([^"]+)"', obsolete_match.group("body")))
    require(
        actual_obsolete == expected_obsolete,
        f"cleanup v1 root allowlist drifted: {sorted(actual_obsolete)}",
    )
    expected_legacy_state = {
        "display-recovery.json",
        "host-settings.json",
        "session.lock",
        "last-command-error.txt",
        "display-driver-verification.json",
        "display-driver-directory-identity.json",
        "backend-lifecycle.json",
        "backend-lifecycle.backup.json",
        "backend-lifecycle.lock",
        "backend-disabled.intent",
        "deferred-host-setup.json",
        "display-suspend.intent",
        "display-suspend.lock",
        "installer-maintenance.json",
        "installer-maintenance.backup.json",
        "installer-maintenance.lock",
        "stream-rescue-status.json",
        "stream-rescue.log",
    }
    legacy_match = re.search(
        r"LegacyProgramDataStateFilesV1\s*=\s*\[(?P<body>.*?)\];",
        residue,
        re.DOTALL,
    )
    require(legacy_match is not None, "cleanup v1 legacy-state allowlist is missing")
    actual_legacy_state = set(
        re.findall(r'"([^"]+)"', legacy_match.group("body"))
    )
    require(
        actual_legacy_state == expected_legacy_state,
        f"cleanup v1 legacy-state allowlist drifted: {sorted(actual_legacy_state)}",
    )
    for file_name in expected_obsolete:
        require(
            f'Type: files; Name: "{{app}}\\{file_name}"' in installer,
            f"uninstall lacks the final exact deletion pass for {file_name}",
        )
    require(
        "CurrentCleanupVersion = 1" in residue
        and "completedVersion < 1" in residue,
        "install cleanup must retain an explicit immutable version boundary",
    )
    require(
        "SearchOption.AllDirectories" not in residue
        and "recursive: true" not in residue
        and "Directory.Delete(testRoot" not in residue,
        "elevated install cleanup must never recursively traverse or delete",
    )
    require(
        "TrustedFileSystem.AcquireDirectoryLease(fullPath)" in residue
        and "TrustedFileSystem.DeleteFile(candidate)" in residue
        and "Refusing cleanup against a filesystem root" in residue
        and "ValidateExactLeafNames(ownedFileNames)" in residue,
        "cleanup must pin each root and delete only validated exact leaf names",
    )
    require(
        ".CleanupObsoleteRootPayloadForUninstall(" in uninstall
        and ".CleanupLegacyProgramDataForUninstall(" in uninstall,
        "uninstall must repeat both exact versioned residue cleanup scopes",
    )


def main() -> int:
    try:
        installer = read(INSTALLER_PATH)
        check_installer_precopy_helper_surface(
            installer,
            read(PROGRAM_PATH),
            read(MAINTENANCE_PATH),
            read(RESCUE_AGENT_PATH),
            read(RECOVERY_TASK_PATH),
        )
        check_installer_failure_ux(installer)
        check_helper_physical_proof(
            read(MAINTENANCE_PATH),
            read(UNINSTALL_PATH),
            read(MANAGED_VDD_RUNTIME_PATH),
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
        check_idle_ownership_release_contract(
            installer,
            read(MAINTENANCE_PATH),
            read(UNINSTALL_PATH),
            read(MANAGED_VDD_RUNTIME_PATH),
            read(DISPLAY_TOPOLOGY_PATH),
            read(DISPLAY_WIZARD_PATH),
            read(PROGRAM_PATH),
        )
        check_versioned_install_cleanup_contract(
            installer,
            read(MACHINE_STATE_PATH),
            read(INSTALL_RESIDUE_PATH),
            read(DISPLAY_WIZARD_PATH),
            read(VDD_NORMALIZER_PATH),
            read(UNINSTALL_PATH),
        )
    except ContractFailure as exc:
        print(f"Windows upgrade contract check failed: {exc}", file=sys.stderr)
        return 1

    print(
        "Windows upgrade contract check passed: protected pre-copy and rollback "
        "mutations use only the current embedded helper and exact installed path, "
        "expected setup failures use a normal failure page and nonzero result, "
        "the embedded helper proves physical-only safety, "
        "interactive tasks cannot bind to different-account UAC, uninstall "
        "remains available, versioned exact install residue is cleaned, and "
        "no unsafe foreground-close rescue exists."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
