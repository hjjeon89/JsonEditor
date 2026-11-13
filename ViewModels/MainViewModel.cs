using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using JsonEditor.Infrastructure;
using JsonEditor.Models;
using JsonEditor.Services;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JsonEditor.ViewModels
{
    public sealed class QuickFilePreset
    {
        public string DisplayName { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
    }

    public sealed class MainViewModel : ObservableObject
    {
        private string _currentFilePath = string.Empty;
        private string _rawJsonText = string.Empty;
        private string _lineNumbersText = "1";
        private string _searchQuery = string.Empty;
        private string _viewMode = "Split"; // Split, Structured, RawCode
        private string _diagnosticFilterMode = "All"; // All, FormatOnly, DuplicatesOnly
        private bool _createBackupOnSave = true;
        private JsonTreeNode? _selectedNode;
        private JsonTreeNode? _activeContainerNode;
        private string _statusMessage = "JSON 파일을 불러오면 구조 트리 생성 및 포맷/중복 정밀 검사를 수행합니다.";
        private string _inspectorBannerTitle = "JSON VALID";
        private string _inspectorBannerDetail = "문법 및 규격 정상";
        private bool _isStrictValid = true;
        private bool _hasParseError;
        private bool _hasFormatWarning;
        private bool _suppressTextToTreeSync;

        // 새 속성 추가 입력값
        private string _newPropKey = string.Empty;
        private string _newPropType = "String";
        private string _newPropValue = string.Empty;

        public MainViewModel()
        {
            InitializeEmptyDocument();
            DiscoverDirectoryJsonFiles(AppDomain.CurrentDomain.BaseDirectory);
            if (QuickFiles.Count > 0 && File.Exists(QuickFiles[0].FullPath))
            {
                LoadJsonFile(QuickFiles[0].FullPath);
            }
        }

        public ObservableCollection<JsonTreeNode> RootNodes { get; } = new();
        public ObservableCollection<JsonDiagnosticItem> Diagnostics { get; } = new();
        public ObservableCollection<JsonDiagnosticItem> FilteredDiagnostics { get; } = new();
        public ObservableCollection<QuickFilePreset> QuickFiles { get; } = new();

        public List<string> AvailableNodeTypes { get; } = new()
        {
            "String",
            "Integer",
            "Float",
            "Boolean",
            "Object",
            "Array",
            "Null"
        };

        public string CurrentFilePath
        {
            get => _currentFilePath;
            set
            {
                if (SetProperty(ref _currentFilePath, value))
                {
                    OnPropertyChanged(nameof(CurrentFileName));
                    OnPropertyChanged(nameof(HasLoadedFile));
                }
            }
        }

        public string CurrentFileName => string.IsNullOrWhiteSpace(_currentFilePath)
            ? "새 JSON 문서"
            : Path.GetFileName(_currentFilePath);

        public bool HasLoadedFile => !string.IsNullOrWhiteSpace(_currentFilePath);

        public string ViewMode
        {
            get => _viewMode;
            set
            {
                if (SetProperty(ref _viewMode, value))
                {
                    OnPropertyChanged(nameof(IsSplitView));
                    OnPropertyChanged(nameof(IsStructuredOnlyView));
                    OnPropertyChanged(nameof(IsRawCodeOnlyView));
                    OnPropertyChanged(nameof(ShowStructuredPane));
                    OnPropertyChanged(nameof(ShowRawCodePane));
                }
            }
        }

        public bool IsSplitView => _viewMode == "Split";
        public bool IsStructuredOnlyView => _viewMode == "Structured";
        public bool IsRawCodeOnlyView => _viewMode == "RawCode";
        public bool ShowStructuredPane => _viewMode is "Split" or "Structured";
        public bool ShowRawCodePane => _viewMode is "Split" or "RawCode";

        public string DiagnosticFilterMode
        {
            get => _diagnosticFilterMode;
            set
            {
                if (SetProperty(ref _diagnosticFilterMode, value))
                {
                    OnPropertyChanged(nameof(IsFilterAll));
                    OnPropertyChanged(nameof(IsFilterFormatOnly));
                    OnPropertyChanged(nameof(IsFilterDuplicatesOnly));
                    RefreshFilteredDiagnostics();
                }
            }
        }

        public bool IsFilterAll => _diagnosticFilterMode == "All";
        public bool IsFilterFormatOnly => _diagnosticFilterMode == "FormatOnly";
        public bool IsFilterDuplicatesOnly => _diagnosticFilterMode == "DuplicatesOnly";

        public string RawJsonText
        {
            get => _rawJsonText;
            set
            {
                if (SetProperty(ref _rawJsonText, value))
                {
                    UpdateLineNumbers(value);
                    if (!_suppressTextToTreeSync)
                    {
                        RunFormatInspection(rebuildTreeIfValid: false);
                    }
                }
            }
        }

        public string LineNumbersText
        {
            get => _lineNumbersText;
            private set => SetProperty(ref _lineNumbersText, value);
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (SetProperty(ref _searchQuery, value))
                {
                    foreach (var root in RootNodes)
                    {
                        root.ApplySearchHighlight(value);
                    }
                }
            }
        }

        public JsonTreeNode? SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (SetProperty(ref _selectedNode, value))
                {
                    if (value != null)
                    {
                        ActiveContainerNode = value.IsContainer ? value : (value.Parent ?? value);
                    }
                    OnPropertyChanged(nameof(HasSelectedNode));
                    OnPropertyChanged(nameof(IsSelectedScalar));
                }
            }
        }

        public bool HasSelectedNode => _selectedNode != null;
        public bool IsSelectedScalar => _selectedNode is { IsScalar: true };

        public JsonTreeNode? ActiveContainerNode
        {
            get => _activeContainerNode;
            set
            {
                if (SetProperty(ref _activeContainerNode, value))
                {
                    OnPropertyChanged(nameof(HasActiveContainer));
                    OnPropertyChanged(nameof(IsActiveContainerObject));
                    OnPropertyChanged(nameof(IsActiveContainerArray));
                }
            }
        }

        public bool HasActiveContainer => _activeContainerNode != null;
        public bool IsActiveContainerObject => _activeContainerNode is { IsObject: true };
        public bool IsActiveContainerArray => _activeContainerNode is { IsArray: true };

        public bool CreateBackupOnSave
        {
            get => _createBackupOnSave;
            set => SetProperty(ref _createBackupOnSave, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public string InspectorBannerTitle
        {
            get => _inspectorBannerTitle;
            private set => SetProperty(ref _inspectorBannerTitle, value);
        }

        public string InspectorBannerDetail
        {
            get => _inspectorBannerDetail;
            private set => SetProperty(ref _inspectorBannerDetail, value);
        }

        public bool IsStrictValid
        {
            get => _isStrictValid;
            private set => SetProperty(ref _isStrictValid, value);
        }

        public bool HasParseError
        {
            get => _hasParseError;
            private set => SetProperty(ref _hasParseError, value);
        }

        public bool HasFormatWarning
        {
            get => _hasFormatWarning;
            private set => SetProperty(ref _hasFormatWarning, value);
        }

        public int ErrorCount => Diagnostics.Count(d => d.IsError) + CountTreeValidationErrors();
        public int WarningCount => Diagnostics.Count(d => d.IsWarning);
        public int DuplicateCount => Diagnostics.Count(d => d.IsDuplicateIssue);
        public int TotalDiagnosticCount => Diagnostics.Count;
        public bool HasNoDiagnostics => FilteredDiagnostics.Count == 0;
        public int ModifiedNodeCount => RootNodes.Count > 0 ? CountModifiedLeafOrContainerNodes(RootNodes[0]) : 0;

        public string NewPropKey
        {
            get => _newPropKey;
            set => SetProperty(ref _newPropKey, value);
        }

        public string NewPropType
        {
            get => _newPropType;
            set => SetProperty(ref _newPropType, value);
        }

        public string NewPropValue
        {
            get => _newPropValue;
            set => SetProperty(ref _newPropValue, value);
        }

        // Commands
        public RelayCommand SwitchViewModeCommand => new(param =>
        {
            if (param is string mode)
                ViewMode = mode;
        });

        public RelayCommand SetDiagnosticFilterCommand => new(param =>
        {
            if (param is string filterMode)
                DiagnosticFilterMode = filterMode;
        });

        public RelayCommand OpenFileCommand => new(() =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "JSON 파일 열기",
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*"
            };

            if (!string.IsNullOrWhiteSpace(CurrentFilePath))
            {
                string? dir = Path.GetDirectoryName(CurrentFilePath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    dlg.InitialDirectory = dir;
            }

            if (dlg.ShowDialog() == true)
            {
                LoadJsonFile(dlg.FileName);
            }
        });

        public RelayCommand LoadQuickFileCommand => new(param =>
        {
            if (param is QuickFilePreset preset && File.Exists(preset.FullPath))
            {
                LoadJsonFile(preset.FullPath);
            }
        });

        public RelayCommand ReloadFileCommand => new(() =>
        {
            if (!string.IsNullOrWhiteSpace(CurrentFilePath) && File.Exists(CurrentFilePath))
            {
                LoadJsonFile(CurrentFilePath);
            }
        });

        public RelayCommand RunFormatCheckCommand => new(() =>
        {
            DiagnosticFilterMode = "All";
            RunFormatInspection(rebuildTreeIfValid: true);
            StatusMessage = $"포맷 및 문법 정밀 검사 완료 ({DateTime.Now:HH:mm:ss}) — 오류 {ErrorCount}건, 규격 주의 {WarningCount}건, 중복 감지 {DuplicateCount}건";
        });

        public RelayCommand RunDuplicateCheckCommand => new(() =>
        {
            var report = RunFormatInspection(rebuildTreeIfValid: true);
            DiagnosticFilterMode = report.DuplicateCount > 0 ? "DuplicatesOnly" : "All";

            if (ViewMode == "Structured")
            {
                ViewMode = "Split";
            }

            int dupKeyCount = report.Diagnostics.Count(d => d.RuleCode == "JSON-DUP-001");
            int dupArrItemCount = report.Diagnostics.Count(d => d.RuleCode == "JSON-DUP-002");
            int dupIdCount = report.Diagnostics.Count(d => d.RuleCode == "JSON-DUP-003");
            int dupValCount = report.Diagnostics.Count(d => d.RuleCode == "JSON-DUP-004");

            StatusMessage = report.DuplicateCount > 0
                ? $"중복 검사 완료 ({DateTime.Now:HH:mm:ss}) — 총 {report.DuplicateCount}건 감지 (중복 키 {dupKeyCount}건, 배열 원소 완전 중복 {dupArrItemCount}건, 식별자/리소스 중복 {dupIdCount}건, 속성값 중복 {dupValCount}건)"
                : $"중복 검사 완료 ({DateTime.Now:HH:mm:ss}) — 중복된 키, 배열 원소, 고유 식별자 및 속성값이 없습니다 (0건).";
        });

        public RelayCommand AutoFixFormatCommand => new(() =>
        {
            if (JsonFormatValidator.TryAutoFixAndFormat(RawJsonText, out string fixedJson, out string msg))
            {
                _suppressTextToTreeSync = true;
                RawJsonText = fixedJson;
                _suppressTextToTreeSync = false;

                RunFormatInspection(rebuildTreeIfValid: true);
                StatusMessage = msg;
            }
            else
            {
                RunFormatInspection(rebuildTreeIfValid: false);
                MessageBox.Show(
                    msg + "\n\n우측 하단 'JSON 포맷 및 중복 검사 리포트'의 줄 번호를 확인하여 문법 오류를 수정해 주세요.",
                    "자동 교정 안내",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        });

        public RelayCommand SyncTextToTreeCommand => new(() =>
        {
            var report = RunFormatInspection(rebuildTreeIfValid: true);
            if (!report.IsParseableByNewtonsoft)
            {
                MessageBox.Show(
                    "현재 JSON 원문에 문법 오류가 있어 구조 트리로 변환할 수 없습니다.\n검사 리포트의 오류 위치를 확인하거나 '자동 교정 및 정렬'을 실행해 보세요.",
                    "JSON 파싱 오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                StatusMessage = "JSON 원문 변경사항이 좌측 구조 트리에 반영되었습니다.";
            }
        });

        public RelayCommand ExpandAllCommand => new(() =>
        {
            foreach (var root in RootNodes)
                root.SetExpandAll(true);
        });

        public RelayCommand CollapseAllCommand => new(() =>
        {
            foreach (var root in RootNodes)
            {
                root.SetExpandAll(false);
                root.IsExpanded = true;
            }
        });

        public RelayCommand ClearSearchCommand => new(() =>
        {
            SearchQuery = string.Empty;
        });

        public RelayCommand SelectNodeCommand => new(param =>
        {
            if (param is JsonTreeNode node)
            {
                SelectedNode = node;
                node.IsExpanded = true;
            }
        });

        public RelayCommand NavigateUpCommand => new(() =>
        {
            if (ActiveContainerNode?.Parent != null)
            {
                SelectedNode = ActiveContainerNode.Parent;
            }
        });

        public RelayCommand AddPropertyToActiveObjectCommand => new(() =>
        {
            if (ActiveContainerNode == null || !ActiveContainerNode.IsObject)
                return;

            var added = ActiveContainerNode.AddObjectProperty(NewPropKey, NewPropType, NewPropValue, out string? error);
            if (error != null)
            {
                MessageBox.Show(error, "속성 추가 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            NewPropKey = string.Empty;
            NewPropValue = string.Empty;
            if (added != null)
                SelectedNode = added;
        });

        public RelayCommand RevertAllCommand => new(() =>
        {
            if (RootNodes.Count == 0)
                return;

            RootNodes[0].RevertCommand.Execute(null);
            SelectedNode = RootNodes[0];
            SyncTreeToRawText();
            StatusMessage = "모든 항목을 처음 불러온 원본 상태로 복구했습니다.";
        });

        public RelayCommand SaveFileCommand => new(() => SaveToFile(CurrentFilePath));

        public RelayCommand SaveAsFileCommand => new(() =>
        {
            var dlg = new SaveFileDialog
            {
                Title = "JSON 파일 다른 이름으로 저장",
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                FileName = string.IsNullOrWhiteSpace(CurrentFilePath) ? "config.json" : Path.GetFileName(CurrentFilePath)
            };

            if (!string.IsNullOrWhiteSpace(CurrentFilePath))
            {
                string? dir = Path.GetDirectoryName(CurrentFilePath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    dlg.InitialDirectory = dir;
            }

            if (dlg.ShowDialog() == true)
            {
                SaveToFile(dlg.FileName);
            }
        });

        public RelayCommand NewDocumentCommand => new(() =>
        {
            InitializeEmptyDocument();
        });

        public void InitializeEmptyDocument()
        {
            CurrentFilePath = string.Empty;
            _suppressTextToTreeSync = true;
            RawJsonText = "{\n\n}";
            _suppressTextToTreeSync = false;

            RunFormatInspection(rebuildTreeIfValid: true);
            StatusMessage = "새 빈 JSON 문서가 준비되었습니다. '파일 열기...' 또는 파일을 드래그 앤 드롭하거나 직접 속성을 추가하세요.";
        }

        public void SelectNodeByJsonPath(string? jsonPath)
        {
            if (string.IsNullOrWhiteSpace(jsonPath) || RootNodes.Count == 0)
                return;

            var found = FindNodeByPath(RootNodes[0], jsonPath);
            if (found != null)
            {
                // 상위 노드들을 모두 펼쳐서 트리에서 보이도록 처리
                var cur = found.Parent;
                while (cur != null)
                {
                    cur.IsExpanded = true;
                    cur = cur.Parent;
                }
                found.IsSelected = true;
                SelectedNode = found;
            }
        }

        private void DiscoverDirectoryJsonFiles(string? dirPath)
        {
            if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath))
                return;

            try
            {
                QuickFiles.Clear();
                var jsonFiles = Directory.GetFiles(dirPath, "*.json", SearchOption.TopDirectoryOnly)
                    .Where(f => !f.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) &&
                                !f.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) &&
                                !f.EndsWith(".dgspec.json", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).Equals("project.assets.json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(Path.GetFileName)
                    .Take(10);

                foreach (var full in jsonFiles)
                {
                    QuickFiles.Add(new QuickFilePreset
                    {
                        DisplayName = Path.GetFileName(full),
                        FullPath = full
                    });
                }
            }
            catch { }
        }

        public static string ReadFileWithSmartEncoding(string filePath)
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            try
            {
                var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                return utf8Strict.GetString(bytes);
            }
            catch
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(949).GetString(bytes);
            }
        }

        public void LoadJsonFile(string filePath)
        {
            try
            {
                string raw = ReadFileWithSmartEncoding(filePath);
                CurrentFilePath = filePath;
                DiscoverDirectoryJsonFiles(Path.GetDirectoryName(filePath));

                _suppressTextToTreeSync = true;
                RawJsonText = raw;
                _suppressTextToTreeSync = false;

                DiagnosticFilterMode = "All";
                var report = RunFormatInspection(rebuildTreeIfValid: true);

                StatusMessage = report.HasErrors
                    ? $"파일 로드됨 ({Path.GetFileName(filePath)}) — 문법 오류 {report.ErrorCount}건 감지됨! 우측 하단 검사 리포트를 확인하세요."
                    : (report.HasWarnings
                        ? $"파일 로드됨 ({Path.GetFileName(filePath)}) — 포맷/중복 주의사항 {report.WarningCount}건 감지됨 ('자동 교정 및 정렬' 또는 리포트 확인)"
                        : $"파일 로드 및 포맷 검증 완료 ({Path.GetFileName(filePath)}) — 문법 및 규격 100% 정상 (중복 참고 {report.DuplicateCount}건)");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"파일을 읽는 중 오류가 발생했습니다:\n{ex.Message}", "파일 열기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public JsonValidationReport RunFormatInspection(bool rebuildTreeIfValid)
        {
            var report = JsonFormatValidator.Inspect(_rawJsonText);

            Diagnostics.Clear();
            foreach (var d in report.Diagnostics)
            {
                Diagnostics.Add(d);
            }

            RefreshFilteredDiagnostics();

            if (rebuildTreeIfValid && report.ParsedToken != null)
            {
                string? previousSelectedPath = SelectedNode?.JsonPath;
                RootNodes.Clear();
                var rootNode = new JsonTreeNode("Root", report.ParsedToken, null, OnTreeMutated, 0)
                {
                    IsExpanded = true
                };
                RootNodes.Add(rootNode);

                SelectedNode = FindNodeByPath(rootNode, previousSelectedPath) ?? rootNode;
            }

            UpdateBannerState(report);
            return report;
        }

        private void RefreshFilteredDiagnostics()
        {
            FilteredDiagnostics.Clear();
            IEnumerable<JsonDiagnosticItem> query = _diagnosticFilterMode switch
            {
                "DuplicatesOnly" => Diagnostics.Where(d => d.IsDuplicateIssue),
                "FormatOnly" => Diagnostics.Where(d => !d.IsDuplicateIssue),
                _ => Diagnostics
            };

            foreach (var item in query)
            {
                FilteredDiagnostics.Add(item);
            }

            OnPropertyChanged(nameof(HasNoDiagnostics));
        }

        private static JsonTreeNode? FindNodeByPath(JsonTreeNode current, string? targetPath)
        {
            if (string.IsNullOrEmpty(targetPath))
                return null;
            if (current.JsonPath == targetPath)
                return current;

            foreach (var child in current.Children)
            {
                var found = FindNodeByPath(child, targetPath);
                if (found != null)
                    return found;
            }
            return null;
        }

        private void OnTreeMutated()
        {
            SyncTreeToRawText();
        }

        private void SyncTreeToRawText()
        {
            if (RootNodes.Count == 0)
                return;

            try
            {
                _suppressTextToTreeSync = true;
                JToken token = RootNodes[0].ToJToken();
                RawJsonText = token.ToString(Formatting.Indented);
            }
            finally
            {
                _suppressTextToTreeSync = false;
            }

            var report = JsonFormatValidator.Inspect(_rawJsonText);
            Diagnostics.Clear();
            foreach (var d in report.Diagnostics)
            {
                Diagnostics.Add(d);
            }
            RefreshFilteredDiagnostics();
            UpdateBannerState(report);
        }

        private void UpdateBannerState(JsonValidationReport report)
        {
            int treeErrs = CountTreeValidationErrors();
            int totalErrors = report.ErrorCount + treeErrs;
            int totalWarnings = report.WarningCount;

            HasParseError = totalErrors > 0;
            HasFormatWarning = totalErrors == 0 && totalWarnings > 0;
            IsStrictValid = totalErrors == 0 && totalWarnings == 0;

            if (totalErrors > 0)
            {
                InspectorBannerTitle = $"INVALID JSON ({totalErrors} ERROR)";
                InspectorBannerDetail = Diagnostics.FirstOrDefault(d => d.IsError)?.Title
                                        ?? "숫자/불리언 타입 입력 오류가 있습니다.";
            }
            else if (totalWarnings > 0)
            {
                InspectorBannerTitle = $"FORMAT / DUP WARNING ({totalWarnings})";
                InspectorBannerDetail = Diagnostics.FirstOrDefault(d => d.IsWarning)?.Title
                                        ?? "표준 JSON 규격 또는 중복 주의사항이 있습니다.";
            }
            else
            {
                InspectorBannerTitle = "VALID JSON (RFC 8259 OK)";
                InspectorBannerDetail = report.DuplicateCount > 0
                    ? $"문법/규격 정상 · 값 중복 참고 {report.DuplicateCount}건"
                    : "문법 오류 및 규격 위반 0건 · 파싱 안전 보장";
            }

            OnPropertyChanged(nameof(ErrorCount));
            OnPropertyChanged(nameof(WarningCount));
            OnPropertyChanged(nameof(DuplicateCount));
            OnPropertyChanged(nameof(TotalDiagnosticCount));
            OnPropertyChanged(nameof(HasNoDiagnostics));
            OnPropertyChanged(nameof(ModifiedNodeCount));
        }

        private void UpdateLineNumbers(string text)
        {
            int lineCount = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                    lineCount++;
            }

            var sb = new StringBuilder(lineCount * 4);
            for (int i = 1; i <= lineCount; i++)
            {
                if (i > 1)
                    sb.Append('\n');
                sb.Append(i);
            }
            LineNumbersText = sb.ToString();
        }

        private int CountTreeValidationErrors()
        {
            if (RootNodes.Count == 0)
                return 0;
            return CountNodeErrorsRecursive(RootNodes[0]);
        }

        private static int CountNodeErrorsRecursive(JsonTreeNode node)
        {
            int c = string.IsNullOrEmpty(node.ValidationError) ? 0 : 1;
            foreach (var child in node.Children)
                c += CountNodeErrorsRecursive(child);
            return c;
        }

        private static int CountModifiedLeafOrContainerNodes(JsonTreeNode node)
        {
            if (node.IsScalar)
                return node.IsModified ? 1 : 0;

            int sum = 0;
            foreach (var child in node.Children)
                sum += CountModifiedLeafOrContainerNodes(child);
            return sum;
        }

        public void SaveToFile(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                SaveAsFileCommand.Execute(null);
                return;
            }

            var report = RunFormatInspection(rebuildTreeIfValid: false);
            int treeErrs = CountTreeValidationErrors();

            if (report.HasErrors || !report.IsParseableByNewtonsoft || treeErrs > 0)
            {
                var firstErr = report.Diagnostics.FirstOrDefault(d => d.IsError);
                string detail = firstErr != null
                    ? $"[{firstErr.LocationBadge}] {firstErr.Title}\n{firstErr.Description}"
                    : "트리 편집기에 잘못된 숫자 형식이 입력되어 있습니다.";

                MessageBox.Show(
                    $"JSON 포맷/문법 오류가 존재하여 저장이 차단되었습니다.\n" +
                    $"(잘못된 JSON을 저장하면 프로그램 로드 시 파싱 에러가 발생합니다)\n\n" +
                    $"오류 내역:\n{detail}",
                    "JSON 파싱 에러 방지 — 저장 차단",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            string jsonToWrite = _rawJsonText;
            if (report.ParsedToken != null)
            {
                jsonToWrite = report.ParsedToken.ToString(Formatting.Indented);
            }

            try
            {
                if (CreateBackupOnSave && File.Exists(targetPath))
                {
                    File.Copy(targetPath, targetPath + ".bak", overwrite: true);
                }

                File.WriteAllText(targetPath, jsonToWrite, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                LoadJsonFile(targetPath);

                StatusMessage = CreateBackupOnSave
                    ? $"표준 JSON 포맷으로 안전하게 저장되었습니다 ({DateTime.Now:HH:mm:ss}) · 백업 생성됨: {Path.GetFileName(targetPath)}.bak"
                    : $"표준 JSON 포맷으로 안전하게 저장되었습니다 ({DateTime.Now:HH:mm:ss})";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"파일 저장 중 오류가 발생했습니다:\n{ex.Message}", "저장 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
