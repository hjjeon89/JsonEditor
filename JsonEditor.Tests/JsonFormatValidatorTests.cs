using System.Linq;
using JsonEditor.Services;
using Xunit;

namespace JsonEditor.Tests
{
    public class JsonFormatValidatorTests
    {
        [Fact]
        public void UnescapedBackslashInWindowsPath_ShouldBeDetectedAsError_AndAutoFixed()
        {
            string brokenJson = @"{
  ""DriverPath"": ""C:\GreenPower\DriverDll\Mascon.dll"",
  ""ChannelCount"": 4
}";

            var report = JsonFormatValidator.Inspect(brokenJson);

            Assert.True(report.HasErrors);
            Assert.Contains(report.Diagnostics, d => d.RuleCode == "JSON-ESC-001" && d.Line == 2);

            bool fixedOk = JsonFormatValidator.TryAutoFixAndFormat(brokenJson, out string fixedJson, out string msg);
            Assert.True(fixedOk, msg);
            Assert.Contains(@"C:\\GreenPower\\DriverDll\\Mascon.dll", fixedJson);

            var afterReport = JsonFormatValidator.Inspect(fixedJson);
            Assert.False(afterReport.HasErrors);
            Assert.Equal(0, afterReport.WarningCount);
            Assert.True(afterReport.IsStrictStandardJson);
        }

        [Fact]
        public void DuplicateKeysInSameObject_ShouldBeDetectedWithExactLineAndPath()
        {
            string jsonWithDupKey = @"{
  ""ChannelCount"": 4,
  ""UseBMS"": true,
  ""ChannelCount"": 8
}";

            var report = JsonFormatValidator.Inspect(jsonWithDupKey);

            var dupDiag = report.Diagnostics.FirstOrDefault(d => d.RuleCode == "JSON-DUP-001");
            Assert.NotNull(dupDiag);
            Assert.Equal(4, dupDiag!.Line);
            Assert.Contains("ChannelCount", dupDiag.Title);
        }

        [Fact]
        public void TrailingCommasInObjectAndArray_ShouldBeDetected_AndAutoFixed()
        {
            string jsonWithTrailingCommas = @"{
  ""Items"": [
    1,
    2,
  ],
  ""Flag"": true,
}";

            var report = JsonFormatValidator.Inspect(jsonWithTrailingCommas);

            var trailingCommaWarnings = report.Diagnostics.Where(d => d.RuleCode == "JSON-FMT-001").ToList();
            Assert.Equal(2, trailingCommaWarnings.Count);
            Assert.False(report.IsStrictStandardJson);

            bool fixedOk = JsonFormatValidator.TryAutoFixAndFormat(jsonWithTrailingCommas, out string fixedJson, out _);
            Assert.True(fixedOk);

            var afterReport = JsonFormatValidator.Inspect(fixedJson);
            Assert.True(afterReport.IsStrictStandardJson);
            Assert.Empty(afterReport.Diagnostics);
        }

        [Fact]
        public void CommentsAndSingleQuotes_ShouldBeDetectedByLexicalScanner()
        {
            string jsonWithComments = @"{
  // 비표준 라인 주석
  ""Name"": ""Mascon"" /* 블록 주석 */
}";

            var commentReport = JsonFormatValidator.Inspect(jsonWithComments);
            Assert.Equal(2, commentReport.Diagnostics.Count(d => d.RuleCode == "JSON-FMT-002"));

            string jsonWithSingleQuote = @"{
  'Name': 'Mascon'
}";
            var quoteReport = JsonFormatValidator.Inspect(jsonWithSingleQuote);
            Assert.True(quoteReport.HasErrors);
            Assert.Contains(quoteReport.Diagnostics, d => d.RuleCode == "JSON-SYN-002");
        }

        [Fact]
        public void ArraySchemaInconsistency_MissingKeyAndTypeMismatch_ShouldBeDetected()
        {
            string inconsistentArrayJson = @"{
  ""driverConfigs"": [
    {
      ""Name"": ""Mascon1"",
      ""CommType"": 0,
      ""Option"": ""TCP""
    },
    {
      ""Name"": ""Mascon2"",
      ""CommType"": 0,
      ""Option"": ""TCP""
    },
    {
      ""Name"": ""Mascon3"",
      ""CommType"": ""0""
    }
  ]
}";

            var report = JsonFormatValidator.Inspect(inconsistentArrayJson);

            // [2]번 항목에 "Option" 키가 누락된 것(JSON-SCH-001)과 "CommType"이 String으로 들어가 타입이 불일치하는 것(JSON-SCH-002) 모두 감지
            Assert.Contains(report.Diagnostics, d => d.RuleCode == "JSON-SCH-001" && d.JsonPath == "$.driverConfigs[2]");
            Assert.Contains(report.Diagnostics, d => d.RuleCode == "JSON-SCH-002" && d.JsonPath == "$.driverConfigs[2].CommType");
        }

        [Fact]
        public void MissingCommaAndUnclosedString_ShouldReportSyntaxErrorWithLineNumber()
        {
            string missingCommaJson = @"{
  ""A"": 10
  ""B"": 20
}";
            var report1 = JsonFormatValidator.Inspect(missingCommaJson);
            Assert.True(report1.HasErrors);
            Assert.False(report1.IsParseableByNewtonsoft);
            Assert.Contains(report1.Diagnostics, d => d.RuleCode == "JSON-SYN-001" && d.Line == 3);
        }

        [Fact]
        public void CapitalizedBooleanAndNullLiterals_ShouldBeAutoFixed()
        {
            string brokenLiterals = @"{
  ""UseBMS"": True,
  ""UseLin"": False,
  ""FontSetting"": None
}";

            bool ok = JsonFormatValidator.TryAutoFixAndFormat(brokenLiterals, out string fixedJson, out _);
            Assert.True(ok);

            var report = JsonFormatValidator.Inspect(fixedJson);
            Assert.True(report.IsStrictStandardJson);
            Assert.Empty(report.Diagnostics);
        }

        [Fact]
        public void ExactDuplicateElementsInArray_ShouldBeDetectedAsDuplicateIssue()
        {
            string dupArrayJson = @"{
  ""driverConfigs"": [
    {
      ""Name"": ""Mascon"",
      ""Model"": ""MasconDriver"",
      ""Openresource"": ""111.111.111.101,2001"",
      ""MappingChannel"": [ 1 ],
      ""Option"": ""TCP""
    },
    {
      ""Name"": ""Mascon"",
      ""Model"": ""MasconDriver"",
      ""Openresource"": ""111.111.111.101,2001"",
      ""MappingChannel"": [ 1 ],
      ""Option"": ""TCP""
    }
  ],
  ""Channels"": [ 1, 2, 1 ]
}";

            var report = JsonFormatValidator.Inspect(dupArrayJson);

            Assert.True(report.HasDuplicates);
            Assert.Contains(report.Diagnostics, d => d.RuleCode == "JSON-DUP-002" && d.JsonPath == "$.driverConfigs[1]" && d.IsDuplicateIssue);
            Assert.Contains(report.Diagnostics, d => d.RuleCode == "JSON-DUP-002" && d.JsonPath == "$.Channels[2]" && d.IsDuplicateIssue);
        }

        [Fact]
        public void DuplicateUniqueIdentifiersAndResourcesInObjectArray_ShouldBeDetected()
        {
            string dupIdJson = @"{
  ""DinItems"": [
    {
      ""Index"": 1,
      ""Name"": ""SYS ON 신호"",
      ""Active"": false
    },
    {
      ""Index"": 1,
      ""Name"": ""EMG 버튼 온"",
      ""Active"": true
    }
  ],
  ""driverConfigs"": [
    {
      ""Name"": ""CanA"",
      ""Model"": ""VectorCanDriver"",
      ""Openresource"": ""xlCanGP,500000,CAN"",
      ""Option"": ""1""
    },
    {
      ""Name"": ""CanB"",
      ""Model"": ""VectorCanDriver"",
      ""Openresource"": ""xlCanGP,500000,CAN"",
      ""Option"": ""1""
    }
  ]
}";

            var report = JsonFormatValidator.Inspect(dupIdJson);

            Assert.True(report.HasDuplicates);
            Assert.Contains(report.Diagnostics, d => d.RuleCode == "JSON-DUP-003" && d.JsonPath == "$.DinItems[1].Index");
            Assert.Contains(report.Diagnostics, d => d.RuleCode == "JSON-DUP-003" && d.JsonPath == "$.driverConfigs[1]");
        }

        [Fact]
        public void DuplicatePropertyValuesInSameObject_ShouldBeDetectedAsInfoDuplicate()
        {
            string dupValueJson = @"{
  ""SaveDataDir"": ""D:\\GreenPower\\Data"",
  ""SystemLogDir"": ""D:\\GreenPower\\Log"",
  ""SharePath"": ""D:\\GreenPower\\Data""
}";

            var report = JsonFormatValidator.Inspect(dupValueJson);

            Assert.True(report.HasDuplicates);
            var dupValDiag = report.Diagnostics.FirstOrDefault(d => d.RuleCode == "JSON-DUP-004");
            Assert.NotNull(dupValDiag);
            Assert.Equal("$.SharePath", dupValDiag!.JsonPath);
            Assert.True(dupValDiag.IsDuplicateIssue);
            Assert.Equal(DiagnosticSeverity.Info, dupValDiag.Severity);
        }
    }
}
