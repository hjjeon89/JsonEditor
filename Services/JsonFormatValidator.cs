using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JsonEditor.Services
{
    public enum DiagnosticSeverity
    {
        Error,
        Warning,
        Info
    }

    public sealed class JsonDiagnosticItem
    {
        public DiagnosticSeverity Severity { get; init; }
        public string RuleCode { get; init; } = string.Empty;
        public int Line { get; init; }
        public int Column { get; init; }
        public string JsonPath { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public bool CanAutoFix { get; init; }

        public bool IsDuplicateIssue => RuleCode.StartsWith("JSON-DUP-", StringComparison.OrdinalIgnoreCase);

        public string SeverityBadge => IsDuplicateIssue
            ? (Severity == DiagnosticSeverity.Info ? "DUPLICATE (값 중복)" : "DUPLICATE (중복 주의)")
            : Severity switch
            {
                DiagnosticSeverity.Error => "ERROR (파싱 불가)",
                DiagnosticSeverity.Warning => "WARNING (규격 주의)",
                _ => "INFO"
            };

        public bool IsError => Severity == DiagnosticSeverity.Error;
        public bool IsWarning => Severity == DiagnosticSeverity.Warning;
        public bool IsInfo => Severity == DiagnosticSeverity.Info;

        public string LocationBadge => Line > 0 ? $"Line {Line}, Col {Column}" : "Global";
    }

    public sealed class JsonValidationReport
    {
        public bool IsParseableByNewtonsoft { get; set; }
        public bool IsStrictStandardJson { get; set; }
        public JToken? ParsedToken { get; set; }
        public List<JsonDiagnosticItem> Diagnostics { get; } = new();

        public int ErrorCount => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        public int WarningCount => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        public int DuplicateCount => Diagnostics.Count(d => d.IsDuplicateIssue);
        public bool HasErrors => ErrorCount > 0;
        public bool HasWarnings => WarningCount > 0;
        public bool HasDuplicates => DuplicateCount > 0;
        public bool CanAutoFixAny => Diagnostics.Any(d => d.CanAutoFix);
    }

    /// <summary>
    /// JSON 문법, 표준 RFC 8259 규격 위반(후행 쉼표, 주석, 중복 키, 이스케이프 누락),
    /// 배열 내 객체 스키마/타입 불일치, 그리고 다계층 중복 검사(동일 객체 내 중복 키/중복 값, 배열 내 완전 중복 원소 및 고유 식별자 중복)를 수행합니다.
    /// </summary>
    public static class JsonFormatValidator
    {
        private static readonly HashSet<string> UniqueIdentifierPropertyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "id",
            "uid",
            "uuid",
            "key",
            "index",
            "idx",
            "no",
            "code",
            "faultcode",
            "alarmcode",
            "processname",
            "schedulename",
            "itemname",
            "filepath",
            "uniquecode"
        };

        private static readonly HashSet<string> IgnoredCommonDuplicateStringValues = new(StringComparer.OrdinalIgnoreCase)
        {
            "auto",
            "none",
            "null",
            "true",
            "false",
            "server",
            "client",
            "tcp",
            "udp",
            "fault",
            "warning",
            "info",
            "default",
            "normal"
        };

        public static JsonValidationReport Inspect(string rawJson)
        {
            var report = new JsonValidationReport();

            if (string.IsNullOrWhiteSpace(rawJson))
            {
                report.Diagnostics.Add(new JsonDiagnosticItem
                {
                    Severity = DiagnosticSeverity.Error,
                    RuleCode = "JSON-EMPTY",
                    Line = 1,
                    Column = 1,
                    Title = "빈 JSON 문서",
                    Description = "JSON 내용이 비어 있습니다. 최소한 '{}' 또는 '[]' 구조가 필요합니다."
                });
                return report;
            }

            // 1단계: 어휘(Lexical) 스캔 — 문자열 내 역슬래시(\) 이스케이프 누락, 후행 쉼표, 주석, 작은따옴표 등 검사
            ScanLexicalIssues(rawJson, report.Diagnostics);

            // 2단계: 스트림 리더(JsonTextReader) 기반 문법 에러 및 동일 객체 내 중복 키(Duplicate Key, JSON-DUP-001) 검사
            JToken? parsedRoot = null;
            try
            {
                CheckDuplicateKeysAndSyntax(rawJson, report.Diagnostics);

                using var sr = new StringReader(rawJson);
                using var jr = new JsonTextReader(sr);
                var settings = new JsonLoadSettings
                {
                    CommentHandling = CommentHandling.Ignore,
                    LineInfoHandling = LineInfoHandling.Load,
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace
                };
                parsedRoot = JToken.Load(jr, settings);
                report.ParsedToken = parsedRoot;
                report.IsParseableByNewtonsoft = true;
            }
            catch (JsonReaderException jex)
            {
                report.IsParseableByNewtonsoft = false;
                report.Diagnostics.Add(new JsonDiagnosticItem
                {
                    Severity = DiagnosticSeverity.Error,
                    RuleCode = "JSON-SYN-001",
                    Line = jex.LineNumber,
                    Column = jex.LinePosition,
                    JsonPath = jex.Path ?? string.Empty,
                    Title = TranslateJsonErrorTitle(jex.Message),
                    Description = TranslateJsonErrorDescription(jex.Message, jex.LineNumber, jex.LinePosition),
                    CanAutoFix = jex.Message.Contains("escape", StringComparison.OrdinalIgnoreCase) ||
                                 jex.Message.Contains("After parsing", StringComparison.OrdinalIgnoreCase)
                });
            }
            catch (Exception ex)
            {
                report.IsParseableByNewtonsoft = false;
                report.Diagnostics.Add(new JsonDiagnosticItem
                {
                    Severity = DiagnosticSeverity.Error,
                    RuleCode = "JSON-SYN-999",
                    Line = 1,
                    Column = 1,
                    Title = "JSON 파싱 오류",
                    Description = ex.Message
                });
            }

            // 3단계: 파싱에 성공한 경우 배열 일관성 검사 및 구조적 중복 검사(배열 원소 중복, 고유 ID 중복, 객체 내 속성값 중복) 수행
            if (parsedRoot != null)
            {
                InspectArrayConsistency(parsedRoot, "$", report.Diagnostics);
                InspectStructuralDuplicates(parsedRoot, "$", report.Diagnostics);
            }

            // 엄격한 표준 JSON 여부 판정 (에러 0건 & 후행 쉼표/주석 등 규격 경고 0건)
            report.IsStrictStandardJson = report.IsParseableByNewtonsoft &&
                                          report.ErrorCount == 0 &&
                                          !report.Diagnostics.Any(d => d.RuleCode is "JSON-FMT-001" or "JSON-FMT-002");

            return report;
        }

        private static void ScanLexicalIssues(string text, List<JsonDiagnosticItem> diagnostics)
        {
            int line = 1;
            int col = 1;
            bool inString = false;
            bool inLineComment = false;
            bool inBlockComment = false;

            int lastCommaLine = -1;
            int lastCommaCol = -1;
            bool lastSignificantWasComma = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';

                if (c == '\n')
                {
                    if (inLineComment)
                        inLineComment = false;
                    line++;
                    col = 1;
                    continue;
                }
                if (c == '\r')
                {
                    continue;
                }

                if (inLineComment)
                {
                    col++;
                    continue;
                }

                if (inBlockComment)
                {
                    if (c == '*' && next == '/')
                    {
                        inBlockComment = false;
                        i++;
                        col += 2;
                    }
                    else
                    {
                        col++;
                    }
                    continue;
                }

                if (inString)
                {
                    if (c == '\\')
                    {
                        bool validEscape = next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't';
                        if (next == 'u' && i + 5 < text.Length)
                        {
                            string hex = text.Substring(i + 2, 4);
                            validEscape = hex.All(Uri.IsHexDigit);
                        }

                        if (!validEscape)
                        {
                            diagnostics.Add(new JsonDiagnosticItem
                            {
                                Severity = DiagnosticSeverity.Error,
                                RuleCode = "JSON-ESC-001",
                                Line = line,
                                Column = col,
                                Title = $"잘못된 이스케이프 시퀀스 ('\\{next}')",
                                Description = "문자열 안에 단일 역슬래시('\\')가 사용되었습니다. Windows 경로 등을 표기할 때는 반드시 '\\\\' 두 개로 작성해야 파싱 에러가 나지 않습니다.",
                                CanAutoFix = true
                            });
                        }

                        i++;
                        col += 2;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = false;
                    }

                    col++;
                    continue;
                }

                // 문자열 밖(Outside String)
                if (c == '/' && next == '/')
                {
                    diagnostics.Add(new JsonDiagnosticItem
                    {
                        Severity = DiagnosticSeverity.Warning,
                        RuleCode = "JSON-FMT-002",
                        Line = line,
                        Column = col,
                        Title = "비표준 JSON 주석 (//) 사용",
                        Description = "표준 JSON 포맷에서는 주석을 지원하지 않습니다. 일부 엄격한 파서에서 에러가 발생할 수 있습니다.",
                        CanAutoFix = true
                    });
                    inLineComment = true;
                    i++;
                    col += 2;
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    diagnostics.Add(new JsonDiagnosticItem
                    {
                        Severity = DiagnosticSeverity.Warning,
                        RuleCode = "JSON-FMT-002",
                        Line = line,
                        Column = col,
                        Title = "비표준 JSON 블록 주석 (/* */) 사용",
                        Description = "표준 JSON 포맷에서는 블록 주석을 지원하지 않습니다.",
                        CanAutoFix = true
                    });
                    inBlockComment = true;
                    i++;
                    col += 2;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    lastSignificantWasComma = false;
                    col++;
                    continue;
                }

                if (c == '\'')
                {
                    diagnostics.Add(new JsonDiagnosticItem
                    {
                        Severity = DiagnosticSeverity.Error,
                        RuleCode = "JSON-SYN-002",
                        Line = line,
                        Column = col,
                        Title = "작은따옴표(') 사용 위반",
                        Description = "JSON 규격에서 키와 문자열은 반드시 큰따옴표(\")로 감싸야 합니다.",
                        CanAutoFix = false
                    });
                }

                if (!char.IsWhiteSpace(c))
                {
                    if (c == ',')
                    {
                        lastSignificantWasComma = true;
                        lastCommaLine = line;
                        lastCommaCol = col;
                    }
                    else if ((c == '}' || c == ']') && lastSignificantWasComma)
                    {
                        diagnostics.Add(new JsonDiagnosticItem
                        {
                            Severity = DiagnosticSeverity.Warning,
                            RuleCode = "JSON-FMT-001",
                            Line = lastCommaLine,
                            Column = lastCommaCol,
                            Title = $"후행 쉼표 (Trailing Comma) 발견 — '{c}' 직전",
                            Description = $"{lastCommaLine}번째 줄 끝의 쉼표(,) 뒤에 다음 항목 없이 곧바로 '{c}'로 닫히고 있습니다. 표준 JSON 규격에서는 마지막 항목 뒤에 쉼표를 붙이면 안 됩니다.",
                            CanAutoFix = true
                        });
                        lastSignificantWasComma = false;
                    }
                    else
                    {
                        lastSignificantWasComma = false;
                    }
                }

                col++;
            }
        }

        private static void CheckDuplicateKeysAndSyntax(string rawJson, List<JsonDiagnosticItem> diagnostics)
        {
            using var sr = new StringReader(rawJson);
            using var reader = new JsonTextReader(sr);

            var objectKeyStack = new Stack<Dictionary<string, int>>();

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonToken.StartObject:
                        objectKeyStack.Push(new Dictionary<string, int>(StringComparer.Ordinal));
                        break;

                    case JsonToken.EndObject:
                        if (objectKeyStack.Count > 0)
                            objectKeyStack.Pop();
                        break;

                    case JsonToken.PropertyName:
                        string propName = reader.Value?.ToString() ?? string.Empty;
                        if (objectKeyStack.Count > 0)
                        {
                            var currentMap = objectKeyStack.Peek();
                            if (currentMap.TryGetValue(propName, out int firstLine))
                            {
                                diagnostics.Add(new JsonDiagnosticItem
                                {
                                    Severity = DiagnosticSeverity.Warning,
                                    RuleCode = "JSON-DUP-001",
                                    Line = reader.LineNumber,
                                    Column = reader.LinePosition,
                                    JsonPath = BuildDotJsonPath(reader.Path),
                                    Title = $"동일 객체 내 중복 키 발견: \"{propName}\" (첫 선언: Line {firstLine})",
                                    Description = $"'{reader.Path}' 위치에 '{propName}' 키가 중복 정의되어 있습니다. 파일을 불러올 때 Line {firstLine}의 이전 설정값이 덮어써져 유실됩니다.",
                                    CanAutoFix = true
                                });
                            }
                            else
                            {
                                currentMap[propName] = reader.LineNumber;
                            }
                        }
                        break;
                }
            }
        }

        private static string BuildDotJsonPath(string readerPath)
        {
            if (string.IsNullOrWhiteSpace(readerPath))
                return "$";
            return readerPath.StartsWith("$", StringComparison.Ordinal) ? readerPath : "$." + readerPath;
        }

        private static void InspectArrayConsistency(JToken token, string currentPath, List<JsonDiagnosticItem> diagnostics)
        {
            if (token is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    InspectArrayConsistency(prop.Value, $"{currentPath}.{prop.Name}", diagnostics);
                }
            }
            else if (token is JArray arr)
            {
                var childObjects = arr.OfType<JObject>().ToList();
                if (childObjects.Count >= 2 && childObjects.Count == arr.Count)
                {
                    var keyTypeCounts = new Dictionary<string, Dictionary<JTokenType, int>>(StringComparer.Ordinal);
                    var keyPresenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);

                    foreach (var itemObj in childObjects)
                    {
                        foreach (var p in itemObj.Properties())
                        {
                            keyPresenceCounts[p.Name] = keyPresenceCounts.GetValueOrDefault(p.Name) + 1;
                            if (!keyTypeCounts.TryGetValue(p.Name, out var typeMap))
                            {
                                typeMap = new Dictionary<JTokenType, int>();
                                keyTypeCounts[p.Name] = typeMap;
                            }
                            if (p.Value.Type != JTokenType.Null)
                            {
                                typeMap[p.Value.Type] = typeMap.GetValueOrDefault(p.Value.Type) + 1;
                            }
                        }
                    }

                    for (int idx = 0; idx < childObjects.Count; idx++)
                    {
                        var itemObj = childObjects[idx];
                        var (itemLine, itemCol) = GetLineInfo(itemObj);

                        foreach (var (key, count) in keyPresenceCounts)
                        {
                            if (count >= childObjects.Count - 1 && !itemObj.ContainsKey(key))
                            {
                                diagnostics.Add(new JsonDiagnosticItem
                                {
                                    Severity = DiagnosticSeverity.Warning,
                                    RuleCode = "JSON-SCH-001",
                                    Line = itemLine,
                                    Column = itemCol,
                                    JsonPath = $"{currentPath}[{idx}]",
                                    Title = $"배열 원소 [{idx}] 속성 누락: \"{key}\"",
                                    Description = $"'{currentPath}' 배열의 다른 항목들에는 '{key}' 속성이 존재하지만 [{idx}]번 항목에는 누락되어 있습니다.",
                                    CanAutoFix = false
                                });
                            }
                        }

                        foreach (var prop in itemObj.Properties())
                        {
                            if (prop.Value.Type == JTokenType.Null)
                                continue;

                            if (keyTypeCounts.TryGetValue(prop.Name, out var typeMap) && typeMap.Count > 1)
                            {
                                var dominantType = typeMap.OrderByDescending(kv => kv.Value).First().Key;
                                if (prop.Value.Type != dominantType)
                                {
                                    var (pLine, pCol) = GetLineInfo(prop, itemLine, itemCol);
                                    diagnostics.Add(new JsonDiagnosticItem
                                    {
                                        Severity = DiagnosticSeverity.Warning,
                                        RuleCode = "JSON-SCH-002",
                                        Line = pLine,
                                        Column = pCol,
                                        JsonPath = $"{currentPath}[{idx}].{prop.Name}",
                                        Title = $"배열 원소 타입 불일치: \"{prop.Name}\" ({prop.Value.Type} ≠ {dominantType})",
                                        Description = $"'{currentPath}[{idx}].{prop.Name}'의 타입이 {prop.Value.Type}입니다. 다른 항목들의 타입({dominantType})과 달라 파싱 시 타입 변환 에러가 날 수 있습니다.",
                                        CanAutoFix = false
                                    });
                                }
                            }
                        }
                    }
                }

                for (int i = 0; i < arr.Count; i++)
                {
                    InspectArrayConsistency(arr[i], $"{currentPath}[{i}]", diagnostics);
                }
            }
        }

        /// <summary>
        /// 구조적 중복 검사:
        /// - JSON-DUP-002: 배열 내 완전히 동일한 원소 중복 (객체 배열 및 스칼라 배열)
        /// - JSON-DUP-003: 배열 내 객체 간 고유 식별자(ID/Index/No/FaultCode 등) 및 리소스 매핑 중복
        /// - JSON-DUP-004: 동일 객체 내 서로 다른 속성이 동일한 경로/문자열 값을 가지는 경우
        /// </summary>
        private static void InspectStructuralDuplicates(JToken token, string currentPath, List<JsonDiagnosticItem> diagnostics)
        {
            if (token is JObject obj)
            {
                // JSON-DUP-004: 동일 객체 내에서 서로 다른 키가 동일한 비자명(non-trivial) 문자열 값을 가지는지 검사
                var seenStringValues = new Dictionary<string, (string PropName, int Line)>(StringComparer.Ordinal);

                foreach (var prop in obj.Properties())
                {
                    if (prop.Value is JValue { Type: JTokenType.String } sVal)
                    {
                        string text = (sVal.Value?.ToString() ?? string.Empty).Trim();
                        // 의미 있는 길이(4자 이상)이고 단순 모드/키워드가 아니며 경로 구분자나 파일/리소스 형태이거나 6자 이상인 값 검사
                        if (text.Length >= 4 &&
                            !IgnoredCommonDuplicateStringValues.Contains(text) &&
                            (text.Contains('\\') || text.Contains('/') || text.Contains('.') || text.Contains(',') || text.Length >= 8))
                        {
                            var (line, col) = GetLineInfo(prop);
                            if (seenStringValues.TryGetValue(text, out var firstProp))
                            {
                                diagnostics.Add(new JsonDiagnosticItem
                                {
                                    Severity = DiagnosticSeverity.Info,
                                    RuleCode = "JSON-DUP-004",
                                    Line = line,
                                    Column = col,
                                    JsonPath = $"{currentPath}.{prop.Name}",
                                    Title = $"동일 객체 내 속성값 중복: \"{firstProp.PropName}\" == \"{prop.Name}\"",
                                    Description = $"'{currentPath}.{prop.Name}'의 값(\"{text}\")이 같은 객체 내 '{firstProp.PropName}'(Line {firstProp.Line}) 속성의 값과 동일합니다.",
                                    CanAutoFix = false
                                });
                            }
                            else
                            {
                                seenStringValues[text] = (prop.Name, line);
                            }
                        }
                    }

                    InspectStructuralDuplicates(prop.Value, $"{currentPath}.{prop.Name}", diagnostics);
                }
            }
            else if (token is JArray arr)
            {
                var exactDupIndices = new HashSet<int>();

                // 1) JSON-DUP-002: 배열 내 100% 동일한 원소 중복 검사
                if (arr.Count >= 2 && arr.Count <= 500)
                {
                    for (int i = 0; i < arr.Count; i++)
                    {
                        if (exactDupIndices.Contains(i))
                            continue;

                        JToken itemI = arr[i];
                        if (IsAllDefaultOrZeroToken(itemI))
                            continue;

                        for (int j = i + 1; j < arr.Count; j++)
                        {
                            if (exactDupIndices.Contains(j))
                                continue;

                            JToken itemJ = arr[j];
                            if (JToken.DeepEquals(itemI, itemJ))
                            {
                                exactDupIndices.Add(j);
                                var (lineI, _) = GetLineInfo(itemI);
                                var (lineJ, colJ) = GetLineInfo(itemJ);

                                string preview = itemJ.Type is JTokenType.Object or JTokenType.Array
                                    ? $"[{i}]번 항목(Line {lineI})과 모든 하위 속성이 일치"
                                    : $"값 '{itemJ}' 중복";

                                diagnostics.Add(new JsonDiagnosticItem
                                {
                                    Severity = DiagnosticSeverity.Warning,
                                    RuleCode = "JSON-DUP-002",
                                    Line = lineJ,
                                    Column = colJ,
                                    JsonPath = $"{currentPath}[{j}]",
                                    Title = $"배열 내 완전 중복 원소 발견: [{i}]번 ↔ [{j}]번",
                                    Description = $"'{currentPath}[{j}]' 원소가 앞선 [{i}]번 원소와 100% 동일합니다 ({preview}). 실수로 복제되었거나 중복 입력된 항목인지 확인하세요.",
                                    CanAutoFix = false
                                });
                            }
                        }
                    }
                }

                // 2) JSON-DUP-003: 객체 배열 내 고유 식별자(ID/Index/No/FaultCode/ItemName 등) 및 복합 리소스 키 중복 검사
                var childObjects = arr.OfType<JObject>().ToList();
                if (childObjects.Count >= 2 && childObjects.Count == arr.Count)
                {
                    InspectObjectArrayIdentifierDuplicates(childObjects, currentPath, exactDupIndices, diagnostics);
                }

                for (int i = 0; i < arr.Count; i++)
                {
                    InspectStructuralDuplicates(arr[i], $"{currentPath}[{i}]", diagnostics);
                }
            }
        }

        private static void InspectObjectArrayIdentifierDuplicates(
            List<JObject> childObjects,
            string currentPath,
            HashSet<int> exactDupIndices,
            List<JsonDiagnosticItem> diagnostics)
        {
            // A. 단일 고유 식별자 키(id, index, no, code, FaultCode, ItemName 등) 중복 검사
            var idValueTrackers = new Dictionary<string, Dictionary<string, (int Index, int Line)>>(StringComparer.OrdinalIgnoreCase);

            // B. 드라이버/통신 설정 복합 키 (Model + Openresource + Option) 중복 검사
            var driverCompositeTracker = new Dictionary<string, (int Index, int Line)>(StringComparer.OrdinalIgnoreCase);

            // C. 센서/채널 복합 키 (UnitNo + ChannelNo + SensorType + ItemIndex) 중복 검사
            var sensorCompositeTracker = new Dictionary<string, (int Index, int Line)>(StringComparer.OrdinalIgnoreCase);

            for (int idx = 0; idx < childObjects.Count; idx++)
            {
                if (exactDupIndices.Contains(idx))
                    continue;

                JObject obj = childObjects[idx];
                var (objLine, objCol) = GetLineInfo(obj);

                foreach (var prop in obj.Properties())
                {
                    if (!UniqueIdentifierPropertyNames.Contains(prop.Name))
                        continue;

                    if (prop.Value is not JValue jv || IsAllDefaultOrZeroToken(jv))
                        continue;

                    string valStr = jv.Value?.ToString()?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(valStr))
                        continue;

                    if (!idValueTrackers.TryGetValue(prop.Name, out var seenValues))
                    {
                        seenValues = new Dictionary<string, (int Index, int Line)>(StringComparer.Ordinal);
                        idValueTrackers[prop.Name] = seenValues;
                    }

                    var (pLine, pCol) = GetLineInfo(prop, objLine, objCol);
                    if (seenValues.TryGetValue(valStr, out var firstSeen))
                    {
                        diagnostics.Add(new JsonDiagnosticItem
                        {
                            Severity = DiagnosticSeverity.Warning,
                            RuleCode = "JSON-DUP-003",
                            Line = pLine,
                            Column = pCol,
                            JsonPath = $"{currentPath}[{idx}].{prop.Name}",
                            Title = $"배열 내 고유 식별자 중복: \"{prop.Name}\" = \"{valStr}\" ([{firstSeen.Index}]번 ↔ [{idx}]번)",
                            Description = $"'{currentPath}[{idx}].{prop.Name}'의 값(\"{valStr}\")이 앞선 [{firstSeen.Index}]번 항목(Line {firstSeen.Line})과 중복됩니다.",
                            CanAutoFix = false
                        });
                    }
                    else
                    {
                        seenValues[valStr] = (idx, pLine);
                    }
                }

                // 복합 키 1: Model + Openresource + Option
                if (obj.TryGetValue("Model", StringComparison.OrdinalIgnoreCase, out var modelTok) &&
                    obj.TryGetValue("Openresource", StringComparison.OrdinalIgnoreCase, out var resTok) &&
                    obj.TryGetValue("Option", StringComparison.OrdinalIgnoreCase, out var optTok))
                {
                    string model = modelTok?.ToString()?.Trim() ?? string.Empty;
                    string res = resTok?.ToString()?.Trim() ?? string.Empty;
                    string opt = optTok?.ToString()?.Trim() ?? string.Empty;

                    if (!string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(res))
                    {
                        string compositeKey = $"{model}|{res}|{opt}";
                        if (driverCompositeTracker.TryGetValue(compositeKey, out var firstDriver))
                        {
                            diagnostics.Add(new JsonDiagnosticItem
                            {
                                Severity = DiagnosticSeverity.Warning,
                                RuleCode = "JSON-DUP-003",
                                Line = objLine,
                                Column = objCol,
                                JsonPath = $"{currentPath}[{idx}]",
                                Title = $"배열 내 동일 드라이버/리소스/옵션 중복 ([{firstDriver.Index}]번 ↔ [{idx}]번)",
                                Description = $"'{currentPath}[{idx}]' 항목의 Model('{model}'), Openresource('{res}'), Option('{opt}') 조합이 [{firstDriver.Index}]번 항목(Line {firstDriver.Line})과 충돌합니다.",
                                CanAutoFix = false
                            });
                        }
                        else
                        {
                            driverCompositeTracker[compositeKey] = (idx, objLine);
                        }
                    }
                }

                // 복합 키 2: UnitNo + ChannelNo + SensorType + ItemIndex
                if (obj.TryGetValue("SensorType", StringComparison.OrdinalIgnoreCase, out var sensorTok) &&
                    obj.TryGetValue("ItemIndex", StringComparison.OrdinalIgnoreCase, out var itemIdxTok))
                {
                    string unit = obj.GetValue("UnitNo", StringComparison.OrdinalIgnoreCase)?.ToString() ?? "0";
                    string ch = obj.GetValue("ChannelNo", StringComparison.OrdinalIgnoreCase)?.ToString() ?? "0";
                    string sensor = sensorTok?.ToString() ?? "0";
                    string itemIdx = itemIdxTok?.ToString() ?? "0";
                    string compositeKey = $"U{unit}|C{ch}|S{sensor}|I{itemIdx}";

                    if (sensorCompositeTracker.TryGetValue(compositeKey, out var firstSensor))
                    {
                        diagnostics.Add(new JsonDiagnosticItem
                        {
                            Severity = DiagnosticSeverity.Warning,
                            RuleCode = "JSON-DUP-003",
                            Line = objLine,
                            Column = objCol,
                            JsonPath = $"{currentPath}[{idx}]",
                            Title = $"배열 내 동일 센서 채널/인덱스 중복 ([{firstSensor.Index}]번 ↔ [{idx}]번)",
                            Description = $"'{currentPath}[{idx}]' 항목의 (UnitNo={unit}, ChannelNo={ch}, SensorType={sensor}, ItemIndex={itemIdx}) 조합이 [{firstSensor.Index}]번 항목(Line {firstSensor.Line})과 중복됩니다.",
                            CanAutoFix = false
                        });
                    }
                    else
                    {
                        sensorCompositeTracker[compositeKey] = (idx, objLine);
                    }
                }
            }
        }

        private static bool IsAllDefaultOrZeroToken(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return true;
                case JTokenType.Boolean:
                    return token.Value<bool>() == false;
                case JTokenType.Integer:
                    return token.Value<long>() == 0L;
                case JTokenType.Float:
                    return Math.Abs(token.Value<double>()) < 1e-12;
                case JTokenType.String:
                    return string.IsNullOrWhiteSpace(token.Value<string>());
                case JTokenType.Array:
                    var arr = (JArray)token;
                    return arr.Count == 0 || arr.All(IsAllDefaultOrZeroToken);
                case JTokenType.Object:
                    var obj = (JObject)token;
                    return !obj.HasValues || obj.Properties().All(p => IsAllDefaultOrZeroToken(p.Value));
                default:
                    return false;
            }
        }

        private static (int Line, int Column) GetLineInfo(JToken token, int fallbackLine = 0, int fallbackCol = 0)
        {
            if (token is IJsonLineInfo li && li.HasLineInfo())
                return (li.LineNumber, li.LinePosition);
            return (fallbackLine, fallbackCol);
        }

        private static string TranslateJsonErrorTitle(string rawMessage)
        {
            if (rawMessage.Contains("Bad JSON escape sequence", StringComparison.OrdinalIgnoreCase))
                return "잘못된 역슬래시(\\) 이스케이프 문법 오류";
            if (rawMessage.Contains("After parsing a value an unexpected character was encountered", StringComparison.OrdinalIgnoreCase))
                return "쉼표(,) 누락 또는 예상치 못한 문자 오류";
            if (rawMessage.Contains("Unterminated string", StringComparison.OrdinalIgnoreCase))
                return "닫히지 않은 문자열 따옴표(\") 오류";
            if (rawMessage.Contains("Unexpected end when deserializing", StringComparison.OrdinalIgnoreCase))
                return "중괄호(}) 또는 대괄호(]) 닫기 누락 오류";
            if (rawMessage.Contains("Invalid property identifier character", StringComparison.OrdinalIgnoreCase))
                return "속성(Key) 이름 따옴표 누락 또는 잘못된 문자";
            if (rawMessage.Contains("Unexpected character encountered while parsing value", StringComparison.OrdinalIgnoreCase))
                return "잘못된 값(Value) 시작 문자 오류";
            return "JSON 문법 파싱 오류";
        }

        private static string TranslateJsonErrorDescription(string rawMessage, int line, int pos)
        {
            if (rawMessage.Contains("Bad JSON escape sequence", StringComparison.OrdinalIgnoreCase))
                return $"{line}번째 줄 {pos}열 부근에서 문자열 내 역슬래시(\\)가 두 번(\\\\) 입력되지 않았습니다. ({rawMessage})";
            if (rawMessage.Contains("After parsing a value an unexpected character was encountered", StringComparison.OrdinalIgnoreCase))
                return $"{line}번째 줄 {pos}열 이전 줄 끝에 쉼표(,)가 빠졌거나 불필요한 문자가 입력되었습니다. ({rawMessage})";
            if (rawMessage.Contains("Unterminated string", StringComparison.OrdinalIgnoreCase))
                return $"{line}번째 줄 {pos}열 부근에서 문자열을 닫는 큰따옴표(\")가 누락되었습니다. ({rawMessage})";
            return $"{line}번째 줄 {pos}열: {rawMessage}";
        }

        /// <summary>
        /// 흔한 JSON 작성 실수(단일 역슬래시 경로, 후행 쉼표, 대소문자 불리언 오타 등)를 자동으로 교정하고
        /// 표준 들여쓰기(Pretty Print) 포맷으로 변환합니다.
        /// </summary>
        public static bool TryAutoFixAndFormat(string rawJson, out string fixedJson, out string summaryMessage)
        {
            fixedJson = rawJson;
            summaryMessage = string.Empty;
            var appliedFixes = new List<string>();

            try
            {
                // 1. 문자열 내부의 잘못된 단일 역슬래시(\) 자동 이스케이프(\\) 교정
                string step1 = FixUnescapedBackslashesInStrings(rawJson, out int escapedCount);
                if (escapedCount > 0)
                    appliedFixes.Add($"역슬래시(\\\\) 이스케이프 {escapedCount}곳 교정");

                // 2. 문자열 외부의 True/False/None 오타 교정
                string step2 = FixBooleanNullLiteralsOutsideStrings(step1, out int literalCount);
                if (literalCount > 0)
                    appliedFixes.Add($"불리언/Null 대소문자 {literalCount}곳 교정");

                // 3. 후행 쉼표 감지 여부 확인 후 JToken 파싱 및 표준 포맷 직렬화
                bool hadTrailingComma = Regex.IsMatch(step2, @",\s*[\}\]]");
                if (hadTrailingComma)
                    appliedFixes.Add("후행 쉼표(Trailing Comma) 제거");

                using var sr = new StringReader(step2);
                using var jr = new JsonTextReader(sr);
                var settings = new JsonLoadSettings
                {
                    CommentHandling = CommentHandling.Ignore,
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace
                };

                JToken token = JToken.Load(jr, settings);
                fixedJson = token.ToString(Formatting.Indented);

                summaryMessage = appliedFixes.Count > 0
                    ? $"자동 교정 완료: {string.Join(", ", appliedFixes)} 및 표준 JSON 정렬 적용"
                    : "표준 JSON 들여쓰기(Pretty Format) 정렬이 적용되었습니다.";
                return true;
            }
            catch (Exception ex)
            {
                summaryMessage = $"자동 교정할 수 없는 문법 오류가 남아 있습니다: {ex.Message}";
                return false;
            }
        }

        private static string FixUnescapedBackslashesInStrings(string input, out int fixedCount)
        {
            fixedCount = 0;
            var sb = new StringBuilder(input.Length + 32);
            bool inString = false;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                char next = i + 1 < input.Length ? input[i + 1] : '\0';

                if (!inString)
                {
                    if (c == '"')
                        inString = true;
                    sb.Append(c);
                }
                else
                {
                    if (c == '\\')
                    {
                        bool validEscape = next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't';
                        if (next == 'u' && i + 5 < input.Length)
                        {
                            string hex = input.Substring(i + 2, 4);
                            validEscape = hex.All(Uri.IsHexDigit);
                        }

                        if (validEscape)
                        {
                            sb.Append(c);
                            sb.Append(next);
                            i++;
                        }
                        else
                        {
                            sb.Append(@"\\");
                            fixedCount++;
                        }
                    }
                    else if (c == '"')
                    {
                        inString = false;
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }

            return sb.ToString();
        }

        private static string FixBooleanNullLiteralsOutsideStrings(string input, out int count)
        {
            int localCount = 0;
            string result = Regex.Replace(
                input,
                @"(?<=[:\[,]\s*)\b(True|False|None)\b(?=\s*[,\}\]\r\n])",
                m =>
                {
                    localCount++;
                    return m.Value switch
                    {
                        "True" => "true",
                        "False" => "false",
                        "None" => "null",
                        _ => m.Value.ToLowerInvariant()
                    };
                });
            count = localCount;
            return result;
        }
    }
}
