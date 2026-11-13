using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using JsonEditor.Models;
using JsonEditor.Services;
using JsonEditor.ViewModels;

namespace JsonEditor
{
    public partial class MainWindow : Window
    {
        private GridLength _savedTreeWidth = new(310, GridUnitType.Pixel);
        private GridLength _savedEditorWidth = new(1.25, GridUnitType.Star);
        private GridLength _savedRawWidth = new(1.0, GridUnitType.Star);
        private string _activeAppliedMode = "Split";

        public MainWindow()
        {
            InitializeComponent();
            DataContextChanged += MainWindow_DataContextChanged;
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += ViewModel_PropertyChanged;
            }
        }

        private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is INotifyPropertyChanged oldVm)
                oldVm.PropertyChanged -= ViewModel_PropertyChanged;
            if (e.NewValue is INotifyPropertyChanged newVm)
                newVm.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.ViewMode) && sender is MainViewModel vm)
            {
                ApplyWorkspaceColumnLayout(vm.ViewMode);
            }
        }

        private void ApplyWorkspaceColumnLayout(string newMode)
        {
            // 현재 모드에서 사용자가 GridSplitter로 조절해 둔 컬럼 폭 저장
            if (_activeAppliedMode == "Split")
            {
                if (TreeColumn.Width.Value > 0)
                    _savedTreeWidth = TreeColumn.Width;
                if (SafeEditorColumn.Width.Value > 0)
                    _savedEditorWidth = SafeEditorColumn.Width;
                if (RawJsonColumn.Width.Value > 0)
                    _savedRawWidth = RawJsonColumn.Width;
            }
            else if (_activeAppliedMode == "Structured")
            {
                if (TreeColumn.Width.Value > 0)
                    _savedTreeWidth = TreeColumn.Width;
            }

            _activeAppliedMode = newMode;

            switch (newMode)
            {
                case "Structured":
                    TreeColumn.MinWidth = 180;
                    TreeColumn.Width = _savedTreeWidth;
                    TreeEditorSplitterColumn.Width = new GridLength(12, GridUnitType.Pixel);
                    SafeEditorColumn.MinWidth = 260;
                    SafeEditorColumn.Width = new GridLength(1, GridUnitType.Star);
                    EditorRawSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                    RawJsonColumn.MinWidth = 0;
                    RawJsonColumn.Width = new GridLength(0, GridUnitType.Pixel);
                    break;

                case "RawCode":
                    TreeColumn.MinWidth = 0;
                    TreeColumn.Width = new GridLength(0, GridUnitType.Pixel);
                    TreeEditorSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                    SafeEditorColumn.MinWidth = 0;
                    SafeEditorColumn.Width = new GridLength(0, GridUnitType.Pixel);
                    EditorRawSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                    RawJsonColumn.MinWidth = 260;
                    RawJsonColumn.Width = new GridLength(1, GridUnitType.Star);
                    break;

                default: // Split
                    TreeColumn.MinWidth = 180;
                    TreeColumn.Width = _savedTreeWidth;
                    TreeEditorSplitterColumn.Width = new GridLength(12, GridUnitType.Pixel);
                    SafeEditorColumn.MinWidth = 260;
                    SafeEditorColumn.Width = _savedEditorWidth;
                    EditorRawSplitterColumn.Width = new GridLength(12, GridUnitType.Pixel);
                    RawJsonColumn.MinWidth = 260;
                    RawJsonColumn.Width = _savedRawWidth;
                    break;
            }
        }

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is MainViewModel vm && e.NewValue is JsonTreeNode node)
            {
                vm.SelectedNode = node;
            }
        }

        private void RawJsonTextBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            LineNumberScrollViewer?.ScrollToVerticalOffset(e.VerticalOffset);
        }

        private void DiagnosticItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is JsonDiagnosticItem diag)
            {
                if (DataContext is MainViewModel vm && !string.IsNullOrWhiteSpace(diag.JsonPath))
                {
                    vm.SelectNodeByJsonPath(diag.JsonPath);
                }

                if (diag.Line > 0)
                {
                    JumpToLineInRawEditor(diag.Line, diag.Column);
                }
            }
        }

        private void JumpToLineInRawEditor(int targetLine, int targetCol)
        {
            if (DataContext is MainViewModel vm && vm.IsStructuredOnlyView)
            {
                vm.ViewMode = "Split";
            }

            string text = RawJsonTextBox.Text ?? string.Empty;
            int currentLine = 1;
            int lineStartIndex = 0;

            for (int i = 0; i < text.Length && currentLine < targetLine; i++)
            {
                if (text[i] == '\n')
                {
                    currentLine++;
                    lineStartIndex = i + 1;
                }
            }

            int lineEndIndex = text.IndexOf('\n', lineStartIndex);
            if (lineEndIndex < 0)
                lineEndIndex = text.Length;

            int selectionLength = Math.Max(0, lineEndIndex - lineStartIndex);
            RawJsonTextBox.Focus();
            RawJsonTextBox.Select(lineStartIndex, selectionLength);
            int lineIndex = Math.Max(0, targetLine - 1);
            if (lineIndex < RawJsonTextBox.LineCount)
            {
                RawJsonTextBox.ScrollToLine(lineIndex);
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                files.Length > 0 &&
                File.Exists(files[0]) &&
                DataContext is MainViewModel vm)
            {
                vm.LoadJsonFile(files[0]);
            }
        }
    }
}
