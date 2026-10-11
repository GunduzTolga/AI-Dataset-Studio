using AIDatasetStudio.Desktop.Controls;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AIDatasetStudio.Desktop.Views;

public sealed partial class RecordDetailsView : UserControl
{
    public RecordDetailsView() => InitializeComponent();

    /// <summary>
    /// Puts the keyboard in the first field that can be written, once the record is laid out
    /// (a new record, M24).
    /// </summary>
    public void FocusFirstField() => Dispatcher.UIThread.Post(
        () =>
        {
            UpdateLayout();
            this.GetVisualDescendants().OfType<CodeEditor>().FirstOrDefault(e => e.IsEffectivelyVisible && !e.IsReadOnly)?.TextArea.Focus();
        },
        DispatcherPriority.Loaded);
}
