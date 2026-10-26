using System;
using System.Diagnostics;
using Unison.Core.Contracts;
using Windows.ApplicationModel.DataTransfer.ShareTarget;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Holds the live <see cref="ShareOperation"/> for the Share Target host window.
    /// Mobile has only that window: <see cref="ShareOperation.ReportCompleted"/> must not
    /// run until the picker/apply path finishes, or the process exits after splash.
    /// </summary>
    /// <remarks>
    /// <see cref="Complete"/> callers: picker cancel, media apply, suspend.
    /// <see cref="Fail"/> also clears the bind after <c>ReportError</c> (and may
    /// fall back to ReportCompleted if ReportError throws).
    /// </remarks>
    public sealed class ShareTargetSession : IShareTargetSession
    {
        private readonly object _gate = new object();
        private ShareOperation _operation;

        public bool IsActive
        {
            get
            {
                lock (_gate)
                {
                    return _operation != null;
                }
            }
        }

        /// <summary>Binds the activation's share operation (replaces any previous).</summary>
        public void Bind(ShareOperation operation)
        {
            lock (_gate)
            {
                _operation = operation;
            }
        }

        public void Complete()
        {
            ShareOperation operation;
            lock (_gate)
            {
                operation = _operation;
                _operation = null;
            }

            if (operation == null)
            {
                return;
            }

            try
            {
                operation.ReportCompleted();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShareTargetSession] ReportCompleted: " + ex.Message);
            }
        }

        /// <summary>Reports a share failure then clears the bound operation.</summary>
        public void Fail(string message)
        {
            ShareOperation operation;
            lock (_gate)
            {
                operation = _operation;
                _operation = null;
            }

            if (operation == null)
            {
                return;
            }

            try
            {
                operation.ReportError(message ?? "Share failed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShareTargetSession] ReportError: " + ex.Message);
                try
                {
                    operation.ReportCompleted();
                }
                catch
                {
                }
            }
        }
    }
}
