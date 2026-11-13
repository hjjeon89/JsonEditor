using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JsonEditor.Infrastructure;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JsonEditor.Models
{
    public enum JsonNodeKind
    {
        Object,
        Array,
        String,
        Integer,
        Float,
        Boolean,
        Null
    }

    public sealed class JsonTreeNode : ObservableObject
    {
        private string _key;
        private JsonNodeKind _nodeKind;
        private string _stringValue = string.Empty;
        private bool _boolValue;
        private string? _validationError;
        private bool _isExpanded;
        private bool _isInlineExpanded;
        private bool _isSelected;
        private bool _isSearchMatch;
        private bool _isModified;

        private readonly JToken _originalToken;
        private readonly JsonNodeKind _originalKind;
        private readonly string _originalScalarString;
        private readonly bool _originalBool;
        private readonly int _originalChildCount;
        private readonly bool _isInitialized;
        private bool _structureMutated;
        private readonly Action _onTreeMutated;

        public JsonTreeNode(
            string key,
            JToken token,
            JsonTreeNode? parent,
            Action onTreeMutated,
            int depth = 0)
        {
            _key = key;
            Parent = parent;
            Depth = depth;
            _onTreeMutated = onTreeMutated;
            _originalToken = token.DeepClone();

            var lineInfo = (IJsonLineInfo)token;
            LineNumber = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;

            _isExpanded = depth == 0;
            LoadFromToken(token, notify: false);

            _originalKind = _nodeKind;
            _originalScalarString = _stringValue;
            _originalBool = _boolValue;
            _originalChildCount = Children.Count;
            _isInitialized = true;
            _isModified = false;
            _isInlineExpanded = IsParentArray && _nodeKind == JsonNodeKind.Object && Children.Count <= 8;
        }

        public JsonTreeNode? Parent { get; }
        public int Depth { get; }
        public int LineNumber { get; private set; }

        public bool IsRoot => Parent == null;
        public bool IsParentArray => Parent?.NodeKind == JsonNodeKind.Array;
        public bool IsParentObject => Parent?.NodeKind == JsonNodeKind.Object;

        public string Key
        {
            get => _key;
            set
            {
                if (SetProperty(ref _key, value))
                {
                    OnPropertyChanged(nameof(DisplayTitle));
                    OnPropertyChanged(nameof(JsonPath));
                    _onTreeMutated?.Invoke();
                }
            }
        }

        public string JsonPath
        {
            get
            {
                if (Parent == null)
                    return "$";
                if (Parent.NodeKind == JsonNodeKind.Array)
                    return $"{Parent.JsonPath}{Key}";
                return Parent.IsRoot ? $"$.{Key}" : $"{Parent.JsonPath}.{Key}";
            }
        }

        public JsonNodeKind NodeKind
        {
            get => _nodeKind;
            set
            {
                if (SetProperty(ref _nodeKind, value))
                {
                    OnPropertyChanged(nameof(TypeBadge));
                    OnPropertyChanged(nameof(IsContainer));
                    OnPropertyChanged(nameof(IsObject));
                    OnPropertyChanged(nameof(IsArray));
                    OnPropertyChanged(nameof(IsScalar));
                    OnPropertyChanged(nameof(IsBoolean));
                    OnPropertyChanged(nameof(IsNumeric));
                    OnPropertyChanged(nameof(IsStringOrNull));
                    OnPropertyChanged(nameof(IsFilePathLike));
                    OnPropertyChanged(nameof(IsFolderPathLike));
                }
            }
        }

        public ObservableCollection<JsonTreeNode> Children { get; } = new();

        public bool IsContainer => NodeKind is JsonNodeKind.Object or JsonNodeKind.Array;
        public bool IsObject => NodeKind == JsonNodeKind.Object;
        public bool IsArray => NodeKind == JsonNodeKind.Array;
        public bool IsScalar => !IsContainer;
        public bool IsBoolean => NodeKind == JsonNodeKind.Boolean;
        public bool IsNumeric => NodeKind is JsonNodeKind.Integer or JsonNodeKind.Float;
        public bool IsStringOrNull => NodeKind is JsonNodeKind.String or JsonNodeKind.Null;

        public bool IsFolderPathLike =>
            NodeKind == JsonNodeKind.String &&
            (Key.EndsWith("Dir", StringComparison.OrdinalIgnoreCase) ||
             Key.EndsWith("DirPath", StringComparison.OrdinalIgnoreCase) ||
             Key.EndsWith("Directory", StringComparison.OrdinalIgnoreCase) ||
             Key.EndsWith("Folder", StringComparison.OrdinalIgnoreCase) ||
             (!string.IsNullOrWhiteSpace(_stringValue) && Regex.IsMatch(_stringValue, @"^[a-zA-Z]:\\") && !Path.HasExtension(_stringValue)));

        public bool IsFilePathLike =>
            NodeKind == JsonNodeKind.String &&
            !IsFolderPathLike &&
            (Key.EndsWith("Path", StringComparison.OrdinalIgnoreCase) ||
             Key.EndsWith("File", StringComparison.OrdinalIgnoreCase) ||
             (!string.IsNullOrWhiteSpace(_stringValue) && Regex.IsMatch(_stringValue, @"^[a-zA-Z]:\\") && Path.HasExtension(_stringValue)));

        public string TypeBadge => NodeKind switch
        {
            JsonNodeKind.Object => $"OBJECT {{{Children.Count}}}",
            JsonNodeKind.Array => $"ARRAY [{Children.Count}]",
            JsonNodeKind.Boolean => "BOOL",
            JsonNodeKind.Integer => "INT",
            JsonNodeKind.Float => "FLOAT",
            JsonNodeKind.Null => "NULL",
            _ => IsFilePathLike ? "STRING (FILE)" : (IsFolderPathLike ? "STRING (DIR)" : "STRING")
        };

        public string DisplayTitle
        {
            get
            {
                if (IsRoot)
                    return NodeKind == JsonNodeKind.Array ? $"Root Array [{Children.Count}]" : $"Root Object {{{Children.Count}}}";

                if (IsParentArray && NodeKind == JsonNodeKind.Object)
                {
                    // 어떤 JSON 배열이든 객체의 첫 1~2개 스칼라 속성을 동적으로 조합하여 표시 (특정 도메인 키에 의존하지 않음)
                    var summaryProps = Children
                        .Where(c => c.IsScalar && !string.IsNullOrWhiteSpace(c.StringValue))
                        .Take(2)
                        .Select(c => $"{c.Key}={c.StringValue}")
                        .ToList();

                    if (summaryProps.Count > 0)
                        return $"{Key}  ({string.Join(", ", summaryProps)})";
                }

                return Key;
            }
        }

        public string ValuePreview
        {
            get
            {
                return NodeKind switch
                {
                    JsonNodeKind.Object => IsParentArray
                        ? string.Join(", ", Children.Take(4).Select(c => $"{c.Key}: {c.ValuePreview}"))
                        : $"하위 속성 {Children.Count}개",
                    JsonNodeKind.Array => Children.Count <= 8 && Children.All(c => c.IsScalar)
                        ? $"[ {string.Join(", ", Children.Select(c => c.ValuePreview))} ]"
                        : $"배열 원소 {Children.Count}개",
                    JsonNodeKind.Boolean => BoolValue ? "true" : "false",
                    JsonNodeKind.Null => "null",
                    JsonNodeKind.String => $"\"{_stringValue}\"",
                    _ => _stringValue
                };
            }
        }

        public string StringValue
        {
            get => _stringValue;
            set
            {
                if (SetProperty(ref _stringValue, value))
                {
                    if (NodeKind == JsonNodeKind.Null && !string.IsNullOrEmpty(value))
                    {
                        NodeKind = JsonNodeKind.String;
                    }
                    ValidateScalar();
                    NotifyNodeChanged();
                }
            }
        }

        public bool BoolValue
        {
            get => _boolValue;
            set
            {
                if (SetProperty(ref _boolValue, value))
                {
                    _stringValue = value ? "true" : "false";
                    OnPropertyChanged(nameof(StringValue));
                    ValidateScalar();
                    NotifyNodeChanged();
                }
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public bool IsInlineExpanded
        {
            get => _isInlineExpanded;
            set => SetProperty(ref _isInlineExpanded, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool IsSearchMatch
        {
            get => _isSearchMatch;
            set => SetProperty(ref _isSearchMatch, value);
        }

        public string? ValidationError
        {
            get => _validationError;
            private set
            {
                if (SetProperty(ref _validationError, value))
                    OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(_validationError) || Children.Any(c => c.HasError);

        public bool IsModified
        {
            get => _isModified;
            private set => SetProperty(ref _isModified, value);
        }

        private void RecalculateIsModified()
        {
            if (!_isInitialized)
            {
                IsModified = false;
                return;
            }

            if (IsScalar)
            {
                if (_nodeKind != _originalKind)
                {
                    IsModified = true;
                    return;
                }
                if (_nodeKind == JsonNodeKind.Boolean)
                {
                    IsModified = _boolValue != _originalBool;
                    return;
                }
                IsModified = !string.Equals(_stringValue, _originalScalarString, StringComparison.Ordinal);
            }
            else
            {
                IsModified = _structureMutated || Children.Count != _originalChildCount || Children.Any(c => c.IsModified);
            }
        }

        public string OriginalValueSummary => _originalScalarString.Length > 90
            ? _originalScalarString[..87] + "..."
            : (IsContainer ? $"원본 하위 항목 {_originalChildCount}개" : _originalScalarString);

        // Commands
        public RelayCommand RevertCommand => new(() =>
        {
            _structureMutated = false;
            LoadFromToken(_originalToken.DeepClone(), notify: true);
        });

        public RelayCommand StepUpCommand => new(() => StepNumeric(+1));
        public RelayCommand StepDownCommand => new(() => StepNumeric(-1));

        public RelayCommand BrowseFileCommand => new(() =>
        {
            var dlg = new OpenFileDialog
            {
                Title = $"{Key} 파일 경로 선택",
                CheckFileExists = false
            };

            if (!string.IsNullOrWhiteSpace(StringValue))
            {
                try
                {
                    string? dir = Path.GetDirectoryName(StringValue);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        dlg.InitialDirectory = dir;
                    dlg.FileName = Path.GetFileName(StringValue);
                }
                catch { }
            }

            if (dlg.ShowDialog() == true)
            {
                StringValue = dlg.FileName;
            }
        });

        public RelayCommand BrowseFolderCommand => new(() =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = $"{Key} 폴더 경로 선택"
            };

            if (!string.IsNullOrWhiteSpace(StringValue) && Directory.Exists(StringValue))
            {
                dlg.InitialDirectory = StringValue;
            }

            if (dlg.ShowDialog() == true)
            {
                StringValue = dlg.FolderName;
            }
        });

        public RelayCommand AddArrayItemCommand => new(() =>
        {
            if (NodeKind != JsonNodeKind.Array)
                return;

            JToken newElementToken = Children.Count > 0
                ? Children[^1].ToJToken().DeepClone()
                : new JValue(string.Empty);

            string nextKey = $"[{Children.Count}]";
            var childNode = new JsonTreeNode(nextKey, newElementToken, this, _onTreeMutated, Depth + 1)
            {
                IsExpanded = true,
                IsInlineExpanded = true
            };
            Children.Add(childNode);
            _structureMutated = true;
            IsExpanded = true;
            NotifyNodeChanged();
        });

        public RelayCommand DuplicateSelfCommand => new(() =>
        {
            if (Parent == null)
                return;

            int idx = Parent.Children.IndexOf(this);
            if (idx < 0)
                return;

            JToken clonedToken = ToJToken().DeepClone();
            string newKey = Parent.NodeKind == JsonNodeKind.Array
                ? $"[{Parent.Children.Count}]"
                : GetUniqueCopyKey(Parent, Key);

            var copyNode = new JsonTreeNode(newKey, clonedToken, Parent, _onTreeMutated, Depth)
            {
                IsExpanded = true,
                IsInlineExpanded = true
            };

            Parent.Children.Insert(idx + 1, copyNode);
            Parent._structureMutated = true;
            Parent.ReindexArrayChildrenIfNeeded();
            Parent.NotifyNodeChanged();
        });

        public RelayCommand DeleteSelfCommand => new(() =>
        {
            if (Parent == null)
                return;

            Parent.Children.Remove(this);
            Parent._structureMutated = true;
            Parent.ReindexArrayChildrenIfNeeded();
            Parent.NotifyNodeChanged();
        });

        public RelayCommand MoveUpCommand => new(() =>
        {
            if (Parent == null)
                return;
            int idx = Parent.Children.IndexOf(this);
            if (idx > 0)
            {
                Parent.Children.Move(idx, idx - 1);
                Parent._structureMutated = true;
                Parent.ReindexArrayChildrenIfNeeded();
                Parent.NotifyNodeChanged();
            }
        });

        public RelayCommand MoveDownCommand => new(() =>
        {
            if (Parent == null)
                return;
            int idx = Parent.Children.IndexOf(this);
            if (idx >= 0 && idx < Parent.Children.Count - 1)
            {
                Parent.Children.Move(idx, idx + 1);
                Parent._structureMutated = true;
                Parent.ReindexArrayChildrenIfNeeded();
                Parent.NotifyNodeChanged();
            }
        });

        private static string GetUniqueCopyKey(JsonTreeNode parentObj, string baseKey)
        {
            int suffix = 1;
            string candidate = $"{baseKey}_copy";
            while (parentObj.Children.Any(c => string.Equals(c.Key, candidate, StringComparison.Ordinal)))
            {
                suffix++;
                candidate = $"{baseKey}_copy{suffix}";
            }
            return candidate;
        }

        public void ReindexArrayChildrenIfNeeded()
        {
            if (NodeKind != JsonNodeKind.Array)
                return;

            for (int i = 0; i < Children.Count; i++)
            {
                Children[i]._key = $"[{i}]";
                Children[i].OnPropertyChanged(nameof(Key));
                Children[i].OnPropertyChanged(nameof(DisplayTitle));
                Children[i].OnPropertyChanged(nameof(JsonPath));
            }
        }

        public JsonTreeNode? AddObjectProperty(string propKey, string typeName, string initialRawValue, out string? error)
        {
            error = null;
            if (NodeKind != JsonNodeKind.Object)
            {
                error = "객체(Object) 노드에만 이름 있는 속성(Key-Value)을 추가할 수 있습니다.";
                return null;
            }

            string trimmedKey = propKey.Trim();
            if (string.IsNullOrEmpty(trimmedKey))
            {
                error = "추가할 속성(Key) 이름을 입력해 주세요.";
                return null;
            }

            if (Children.Any(c => string.Equals(c.Key, trimmedKey, StringComparison.Ordinal)))
            {
                error = $"동일 객체 내에 이미 '{trimmedKey}' 키가 존재합니다 (중복 키 방지).";
                return null;
            }

            JToken token = typeName switch
            {
                "Integer" => long.TryParse(initialRawValue, out long lv) ? new JValue(lv) : new JValue(0L),
                "Float" => double.TryParse(initialRawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv) ? new JValue(dv) : new JValue(0.0),
                "Boolean" => bool.TryParse(initialRawValue, out bool bv) ? new JValue(bv) : new JValue(false),
                "Array" => new JArray(),
                "Object" => new JObject(),
                "Null" => JValue.CreateNull(),
                _ => new JValue(initialRawValue ?? string.Empty)
            };

            var newNode = new JsonTreeNode(trimmedKey, token, this, _onTreeMutated, Depth + 1)
            {
                IsExpanded = true,
                IsInlineExpanded = true
            };
            Children.Add(newNode);
            _structureMutated = true;
            IsExpanded = true;
            NotifyNodeChanged();
            return newNode;
        }

        private void StepNumeric(int delta)
        {
            if (NodeKind == JsonNodeKind.Integer)
            {
                long current = long.TryParse(_stringValue, out long v) ? v : 0;
                StringValue = (current + delta).ToString(CultureInfo.InvariantCulture);
            }
            else if (NodeKind == JsonNodeKind.Float)
            {
                double current = double.TryParse(_stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;
                StringValue = (current + delta).ToString("0.0############", CultureInfo.InvariantCulture);
            }
        }

        public void LoadFromToken(JToken token, bool notify)
        {
            ValidationError = null;
            Children.Clear();

            switch (token.Type)
            {
                case JTokenType.Object:
                    NodeKind = JsonNodeKind.Object;
                    foreach (var prop in ((JObject)token).Properties())
                    {
                        Children.Add(new JsonTreeNode(prop.Name, prop.Value, this, _onTreeMutated, Depth + 1));
                    }
                    break;

                case JTokenType.Array:
                    NodeKind = JsonNodeKind.Array;
                    var arr = (JArray)token;
                    for (int i = 0; i < arr.Count; i++)
                    {
                        Children.Add(new JsonTreeNode($"[{i}]", arr[i], this, _onTreeMutated, Depth + 1));
                    }
                    break;

                case JTokenType.Boolean:
                    NodeKind = JsonNodeKind.Boolean;
                    _boolValue = token.Value<bool>();
                    _stringValue = _boolValue ? "true" : "false";
                    break;

                case JTokenType.Integer:
                    NodeKind = JsonNodeKind.Integer;
                    _stringValue = token.Value<long>().ToString(CultureInfo.InvariantCulture);
                    break;

                case JTokenType.Float:
                    NodeKind = JsonNodeKind.Float;
                    string rawFloat = token.ToString(Formatting.None);
                    _stringValue = rawFloat.Contains('.') || rawFloat.Contains('E') || rawFloat.Contains('e')
                        ? rawFloat
                        : token.Value<double>().ToString("0.0############", CultureInfo.InvariantCulture);
                    break;

                case JTokenType.Null:
                    NodeKind = JsonNodeKind.Null;
                    _stringValue = string.Empty;
                    break;

                default:
                    NodeKind = JsonNodeKind.String;
                    _stringValue = token.Value<string>() ?? string.Empty;
                    break;
            }

            RecalculateIsModified();

            if (notify)
            {
                OnPropertyChanged(nameof(StringValue));
                OnPropertyChanged(nameof(BoolValue));
                NotifyNodeChanged();
            }
        }

        private void ValidateScalar()
        {
            ValidationError = null;

            switch (NodeKind)
            {
                case JsonNodeKind.Integer:
                    if (!long.TryParse(_stringValue.Trim(), out _))
                    {
                        ValidationError = $"정수(Integer) 타입에는 숫자만 입력할 수 있습니다: '{_stringValue}'";
                    }
                    break;

                case JsonNodeKind.Float:
                    if (!double.TryParse(_stringValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    {
                        ValidationError = $"실수(Float) 타입에는 유효한 숫자만 입력할 수 있습니다: '{_stringValue}'";
                    }
                    break;

                case JsonNodeKind.Boolean:
                    if (!string.Equals(_stringValue, "true", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(_stringValue, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        ValidationError = "불리언(Boolean) 값은 true 또는 false 여야 합니다.";
                    }
                    break;
            }
        }

        public void NotifyNodeChanged()
        {
            RecalculateIsModified();
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(ValuePreview));
            OnPropertyChanged(nameof(TypeBadge));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(IsFilePathLike));
            OnPropertyChanged(nameof(IsFolderPathLike));
            Parent?.NotifyParentSummaryOnly();
            _onTreeMutated?.Invoke();
        }

        private void NotifyParentSummaryOnly()
        {
            RecalculateIsModified();
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(ValuePreview));
            OnPropertyChanged(nameof(TypeBadge));
            OnPropertyChanged(nameof(HasError));
            Parent?.NotifyParentSummaryOnly();
        }

        public JToken ToJToken()
        {
            switch (NodeKind)
            {
                case JsonNodeKind.Object:
                    var obj = new JObject();
                    foreach (var child in Children)
                    {
                        obj[child.Key] = child.ToJToken();
                    }
                    return obj;

                case JsonNodeKind.Array:
                    var arr = new JArray();
                    foreach (var child in Children)
                    {
                        arr.Add(child.ToJToken());
                    }
                    return arr;

                case JsonNodeKind.Boolean:
                    return new JValue(BoolValue);

                case JsonNodeKind.Integer:
                    if (long.TryParse(_stringValue.Trim(), out long lVal))
                        return new JValue(lVal);
                    return _originalToken.DeepClone();

                case JsonNodeKind.Float:
                    if (double.TryParse(_stringValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dVal))
                        return new JValue(dVal);
                    return _originalToken.DeepClone();

                case JsonNodeKind.Null:
                    return JValue.CreateNull();

                default:
                    return new JValue(_stringValue);
            }
        }

        public void SetExpandAll(bool expanded)
        {
            IsExpanded = expanded;
            foreach (var child in Children)
            {
                child.SetExpandAll(expanded);
            }
        }

        public bool ApplySearchHighlight(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                IsSearchMatch = false;
                foreach (var child in Children)
                    child.ApplySearchHighlight(string.Empty);
                return false;
            }

            bool selfMatches = Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               (IsScalar && _stringValue.Contains(query, StringComparison.OrdinalIgnoreCase));

            bool anyChildMatches = false;
            foreach (var child in Children)
            {
                if (child.ApplySearchHighlight(query))
                    anyChildMatches = true;
            }

            IsSearchMatch = selfMatches;
            if (anyChildMatches)
                IsExpanded = true;

            return selfMatches || anyChildMatches;
        }
    }
}
