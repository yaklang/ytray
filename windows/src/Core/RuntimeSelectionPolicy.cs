#nullable enable
using System;

namespace YTray.Core
{
    /// <summary>
    /// Keeps runtime selectors aligned with the persisted application default. A cached page may
    /// still hold its old ComboBox selection when it is shown again; that view-local value must
    /// never overwrite a newer default chosen on another page while the list is being rebound.
    /// </summary>
    internal static class RuntimeSelectionPolicy
    {
        internal static Guid? TargetRuntimeId(Guid? configuredDefaultId, Guid? fallbackRuntimeId) =>
            configuredDefaultId ?? fallbackRuntimeId;

        internal static bool ShouldPersistUserSelection(
            bool isRebinding,
            Guid selectedRuntimeId,
            Guid? configuredDefaultId) =>
            !isRebinding && selectedRuntimeId != configuredDefaultId;
    }
}
