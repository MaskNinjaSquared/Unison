using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Background;
using Windows.Networking.Sockets;
using Unison.Background;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Uwp.Helpers;
using Unison.Uwp.Transport;

namespace Unison.Uwp.Services
{
    public sealed class SocketBrokerCoordinator : ISocketBrokerService
    {
        private static readonly SocketBrokerCoordinator _instance = new SocketBrokerCoordinator();
        private const string RegistrationSchemaVersion = "v673b1-r1";
        public static SocketBrokerCoordinator Instance => _instance;

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private IBackgroundTaskRegistration _registration;
        private bool _requestAttempted;
        private BackgroundAccessStatus _accessStatus = BackgroundAccessStatus.Unspecified;

        public Guid TaskId => _registration == null ? Guid.Empty : _registration.TaskId;
        public bool IsReady => _registration != null;
        internal BackgroundAccessStatus AccessStatus => _accessStatus;

        private SocketBrokerCoordinator()
        {
        }

        public async Task<bool> EnsureReadyAsync()
        {
            await _gate.WaitAsync();
            try
            {
                await RemoveRegressionInProcessRegistrationAsync();

                string currentMarker = GetCurrentRegistrationMarker();
                string storedMarker = LoadStoredRegistrationMarker();
                _registration = _registration ?? FindRegistration();
                if (_registration != null)
                {
                    if (string.Equals(
                            storedMarker,
                            currentMarker,
                            StringComparison.Ordinal))
                    {
                        RuntimeDiagnosticsService.Instance.Write(
                            "socket-broker",
                            "task-found",
                            "taskId=" + _registration.TaskId +
                            "; marker=" + currentMarker);
                        return true;
                    }

                    RuntimeDiagnosticsService.Instance.Write(
                        "socket-broker",
                        "task-refresh-required",
                        "reason=package-or-schema-changed" +
                        "; storedMarker=" + (storedMarker ?? "<none>") +
                        "; currentMarker=" + currentMarker +
                        "; taskId=" + _registration.TaskId);
                    await UnregisterCurrentTaskRegistrationsAsync(
                        "package-or-schema-changed");
                    _registration = null;
                }

                return await RegisterCurrentTaskAsync(
                    currentMarker,
                    "ensure-ready");
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException("socket-broker", "task-registration-failed", ex);
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> RecreateRegistrationAsync(string reason)
        {
            await _gate.WaitAsync();
            try
            {
                string effectiveReason = string.IsNullOrWhiteSpace(reason)
                    ? "explicit-recreate"
                    : reason;
                Guid previousTaskId = TaskId;
                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "task-recreate-start",
                    "reason=" + effectiveReason +
                    "; previousTaskId=" + previousTaskId);

                await UnregisterCurrentTaskRegistrationsAsync(
                    effectiveReason);
                _registration = null;
                ClearStoredRegistrationMarker();

                bool registered = await RegisterCurrentTaskAsync(
                    GetCurrentRegistrationMarker(),
                    effectiveReason);
                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "task-recreate-result",
                    "reason=" + effectiveReason +
                    "; registered=" + registered +
                    "; previousTaskId=" + previousTaskId +
                    "; currentTaskId=" + TaskId);
                return registered;
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "socket-broker",
                    "task-recreate-failed",
                    ex);
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static IBackgroundTaskRegistration FindRegistration()
        {
            return BackgroundTaskRegistration.AllTasks
                .Select(pair => pair.Value)
                .FirstOrDefault(task => task.Name == SocketBrokerConstants.TaskName);
        }

        private async Task<bool> RegisterCurrentTaskAsync(
            string registrationMarker,
            string reason)
        {
            if (!_requestAttempted)
            {
                _requestAttempted = true;
                _accessStatus =
                    await BackgroundExecutionManager.RequestAccessAsync();
                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "background-access",
                    "status=" + _accessStatus);
            }

            if (_accessStatus == BackgroundAccessStatus.Denied ||
                _accessStatus == BackgroundAccessStatus.DeniedBySystemPolicy ||
                _accessStatus == BackgroundAccessStatus.DeniedByUser)
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "task-register-denied",
                    "reason=" + (reason ?? string.Empty) +
                    "; status=" + _accessStatus);
                return false;
            }

            var builder = new BackgroundTaskBuilder
            {
                Name = SocketBrokerConstants.TaskName,
                TaskEntryPoint = SocketBrokerConstants.TaskEntryPoint,
                IsNetworkRequested = true
            };
            builder.SetTrigger(new SocketActivityTrigger());
            _registration = builder.Register();
            SaveStoredRegistrationMarker(registrationMarker);
            RuntimeDiagnosticsService.Instance.Write(
                "socket-broker",
                "task-registered",
                "taskId=" + _registration.TaskId +
                "; reason=" + (reason ?? string.Empty) +
                "; marker=" + registrationMarker);
            return true;
        }

        private static Task UnregisterCurrentTaskRegistrationsAsync(
            string reason)
        {
            List<IBackgroundTaskRegistration> registrations =
                BackgroundTaskRegistration.AllTasks
                    .Select(pair => pair.Value)
                    .Where(task => string.Equals(
                        task.Name,
                        SocketBrokerConstants.TaskName,
                        StringComparison.Ordinal))
                    .ToList();

            foreach (IBackgroundTaskRegistration registration in registrations)
            {
                try
                {
                    registration.Unregister(true);
                    RuntimeDiagnosticsService.Instance.Write(
                        "socket-broker",
                        "task-unregistered",
                        "reason=" + (reason ?? string.Empty) +
                        "; taskId=" + registration.TaskId);
                }
                catch (Exception ex)
                {
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "socket-broker",
                        "task-unregister-failed",
                        ex);
                }
            }

            return Task.CompletedTask;
        }

        private static string GetCurrentRegistrationMarker()
        {
            PackageVersion version = Package.Current.Id.Version;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}.{1}.{2}.{3}|{4}|{5}",
                version.Major,
                version.Minor,
                version.Build,
                version.Revision,
                RegistrationSchemaVersion,
                SocketBrokerConstants.TaskEntryPoint);
        }

        private static string LoadStoredRegistrationMarker()
        {
            string value = LocalSettingsAccess.Current.Get<string>(
                LocalSettingsConstants.SocketBrokerTaskRegistrationMarker);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static void SaveStoredRegistrationMarker(string marker)
        {
            LocalSettingsAccess.Current.Set(
                LocalSettingsConstants.SocketBrokerTaskRegistrationMarker,
                marker ?? string.Empty);
        }

        private static void ClearStoredRegistrationMarker()
        {
            LocalSettingsAccess.Current.Remove(
                LocalSettingsConstants.SocketBrokerTaskRegistrationMarker);
        }

        private static async Task RemoveRegressionInProcessRegistrationAsync()
        {
            var registrations = BackgroundTaskRegistration.AllTasks
                .Select(pair => pair.Value)
                .Where(task => string.Equals(
                    task.Name,
                    SocketBrokerConstants.RegressionInProcessTaskName,
                    StringComparison.Ordinal))
                .ToList();

            foreach (IBackgroundTaskRegistration registration in registrations)
            {
                try
                {
                    registration.Unregister(true);
                    RuntimeDiagnosticsService.Instance.Write(
                        "socket-broker",
                        "v672-inprocess-task-removed",
                        "taskId=" + registration.TaskId);
                }
                catch (Exception ex)
                {
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "socket-broker",
                        "v672-inprocess-task-remove-failed",
                        ex);
                }
            }

            if (SocketActivityInformation.AllSockets.ContainsKey(
                SocketBrokerConstants.RegressionInProcessSocketId))
            {
                await DisposeBrokerSocketAsync(
                    SocketBrokerConstants.RegressionInProcessSocketId);
                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "v672-inprocess-socket-removed");
            }
        }

        public static Task DisposeBrokerSocketAsync(string socketId = null)
            => _instance.DisposeBrokerSocketCoreAsync(socketId);

        Task ISocketBrokerService.DisposeBrokerSocketAsync(string socketId)
            => DisposeBrokerSocketCoreAsync(socketId);

        private async Task DisposeBrokerSocketCoreAsync(string socketId = null)
        {
            try
            {
                var socketIds = new System.Collections.Generic.List<string>();
                BrokerOwnershipState persisted =
                    await BrokerOwnershipStore.LoadAsync();
                if (BrokerOwnershipStore.IsManagedSocketId(socketId))
                {
                    socketIds.Add(socketId);
                }
                else
                {
                    socketIds.AddRange(
                        SocketActivityInformation.AllSockets.Keys
                            .Where(BrokerOwnershipStore.IsManagedSocketId));
                    if (persisted != null &&
                        BrokerOwnershipStore.IsManagedSocketId(persisted.SocketId))
                    {
                        socketIds.Add(persisted.SocketId);
                    }
                }

                foreach (string id in socketIds.Distinct(StringComparer.Ordinal))
                {
                    SocketActivityInformation information;
                    if (!SocketActivityInformation.AllSockets.TryGetValue(id, out information))
                    {
                        continue;
                    }

                    StreamSocket socket = information == null
                        ? null
                        : information.StreamSocket;
                    if (socket == null)
                    {
                        continue;
                    }

                    try { await socket.CancelIOAsync(); } catch { }
                    socket.Dispose();
                    RuntimeDiagnosticsService.Instance.Write(
                        "socket-broker",
                        "broker-socket-disposed",
                        "id=" + id);
                }
                if (persisted != null &&
                    socketIds.Any(id => string.Equals(
                        id,
                        persisted.SocketId,
                        StringComparison.Ordinal)))
                {
                    await BrokerOwnershipStore.ClearAsync();
                }
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException("socket-broker", "dispose-broker-socket-failed", ex);
            }
        }
    }
}
