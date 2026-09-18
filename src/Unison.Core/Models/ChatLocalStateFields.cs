using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Which fields of <see cref="ChatLocalState"/> were written on purpose.
    /// </summary>
    /// <remarks>
    /// A mute remembered during sync used to create a cache stub whose pin defaulted to
    /// <c>false</c>. <c>ApplyTo</c> then treated that default as "not pinned" and wiped the
    /// icon off the row. Only fields in this mask are authoritative.
    /// </remarks>
    [Flags]
    public enum ChatLocalStateFields
    {
        None = 0,
        Pin = 1,
        Mute = 2,
        WidgetPin = 4,
        Status = 8,
        All = Pin | Mute | WidgetPin | Status
    }
}
