using AIDatasetStudio.Desktop.ViewModels;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views;

public sealed partial class RecordListView : UserControl
{
    // Start loading the next page while this many screens of rows are still below the viewport.
    private const double PrefetchScreens = 2;

    public RecordListView()
    {
        InitializeComponent();
        RecordList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
    }

    /// <summary>
    /// Mod+F: puts the cursor in the search box.
    /// </summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>
    /// Whether the record list itself (not a text box) has keyboard focus.
    /// </summary>
    public bool IsListFocused => RecordList.IsKeyboardFocusWithin;

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer viewer || DataContext is not RecordListViewModel viewModel)
        {
            return;
        }

        var remaining = viewer.Extent.Height - viewer.Offset.Y - viewer.Viewport.Height;
        if (remaining <= viewer.Viewport.Height * PrefetchScreens && viewModel.LoadMoreCommand.CanExecute(null))
        {
            viewModel.LoadMoreCommand.Execute(null);
        }
    }
}
