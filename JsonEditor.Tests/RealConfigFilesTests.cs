using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JsonEditor.Models;
using JsonEditor.Services;
using JsonEditor.ViewModels;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JsonEditor.Tests
{
    public class RealConfigFilesTests
    {
        public static IEnumerable<object[]> GetAllWorkspaceJsonFiles()
        {
            string repoRoot = @"D:\__work\__src\gitea\ModulePackCycler";
            var files = new[]
            {
                Path.Combine(repoRoot, @"Util\JsonEditor\LocalConfig_ctrl.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\LocalConfig.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\AlarmConfig.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\ChaDischaCalibrationConfig.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\DaqConfig.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\DioConfig.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\EditorLocalConfig.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\ProcessListLocalConfig.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\ScheduleFileDataListConfig.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\DAQ-DDrive\DaqConfig.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\DAQ-DDrive\LocalConfig.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\DAQ-DDrive\Ntc2Config.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\DAQ-DDrive\NtcConfig.json")
            };

            foreach (var f in files)
            {
                yield return new object[] { f };
            }
        }

        [Theory]
        [MemberData(nameof(GetAllWorkspaceJsonFiles))]
        public void AllRealJsonFiles_ShouldParse_BuildTree_AndRoundTripLosslessly(string filePath)
        {
            Assert.True(File.Exists(filePath), $"테스트 대상 파일이 존재하지 않습니다: {filePath}");

            // 1. 인코딩 자동 감지 읽기
            string rawJson = MainViewModel.ReadFileWithSmartEncoding(filePath);
            Assert.False(string.IsNullOrWhiteSpace(rawJson));

            // 2. 포맷 검사 및 파싱 검증
            var report = JsonFormatValidator.Inspect(rawJson);
            Assert.True(report.IsParseableByNewtonsoft, $"{filePath} 파일이 파싱되지 않았습니다.");
            Assert.Equal(0, report.ErrorCount);
            Assert.NotNull(report.ParsedToken);

            // 3. 고정 클래스 없이 동적 트리(JsonTreeNode) 생성 및 역직렬화 무손실(DeepEquals) 검증
            var rootNode = new JsonTreeNode("Root", report.ParsedToken!, null, () => { });
            Assert.False(rootNode.IsModified);
            Assert.False(rootNode.HasError);

            JToken roundTripped = rootNode.ToJToken();
            Assert.True(
                JToken.DeepEquals(report.ParsedToken, roundTripped),
                $"{Path.GetFileName(filePath)}: 원본 JToken과 JsonTreeNode 왕복 변환 결과가 일치하지 않습니다.");

            // 4. 최상위가 JObject인 경우 키(Key) 순서까지 100% 동일한지 검증
            if (report.ParsedToken is JObject origObj && roundTripped is JObject rtObj)
            {
                var origKeys = origObj.Properties().Select(p => p.Name).ToList();
                var rtKeys = rtObj.Properties().Select(p => p.Name).ToList();
                Assert.Equal(origKeys, rtKeys);
            }
        }

        [Theory]
        [MemberData(nameof(GetAllWorkspaceJsonFiles))]
        public void AllRealJsonFiles_WhenLoadedIntoMainViewModel_AndSaved_ShouldPassStrictStandardJsonParser(string filePath)
        {
            var vm = new MainViewModel();
            vm.LoadJsonFile(filePath);

            Assert.Equal(0, vm.ErrorCount);
            Assert.Equal(0, vm.ModifiedNodeCount);
            Assert.Single(vm.RootNodes);

            // 임시 파일로 저장 후 System.Text.Json의 엄격한 기본 설정(AllowTrailingCommas = false)으로도 파싱 에러 없이 읽히는지 검증
            string tempFile = Path.Combine(Path.GetTempPath(), $"jsoneditor_test_{Guid.NewGuid():N}.json");
            try
            {
                vm.CreateBackupOnSave = false;
                vm.SaveToFile(tempFile);

                Assert.True(File.Exists(tempFile));
                string savedContent = File.ReadAllText(tempFile);

                // System.Text.Json 엄격 파서 검증 (후행 쉼표 등이 모두 깨끗하게 정리되어 표준 규격을 만족해야 함)
                using var doc = JsonDocument.Parse(savedContent, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
                Assert.NotNull(doc.RootElement.ToString());

                // 재검사 시 오류 0건, 후행 쉼표 경고 0건이어야 함
                var savedReport = JsonFormatValidator.Inspect(savedContent);
                Assert.True(savedReport.IsStrictStandardJson);
                Assert.Equal(0, savedReport.ErrorCount);
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [Fact]
        public void TrailingCommas_InExistingRepoFiles_ShouldBeAccuratelyDetectedByValidator()
        {
            string repoRoot = @"D:\__work\__src\gitea\ModulePackCycler";

            // AlarmConfig.json, DioConfig.json, monitoring\LocalConfig.json, LocalConfig_ctrl.json 은 원본에 후행 쉼표가 존재함
            string[] filesWithTrailingCommas =
            {
                Path.Combine(repoRoot, @"Util\JsonEditor\LocalConfig_ctrl.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\LocalConfig.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\DioConfig.json"),
                Path.Combine(repoRoot, @"settingFiles\monitoring\AlarmConfig.json")
            };

            foreach (var file in filesWithTrailingCommas)
            {
                string raw = MainViewModel.ReadFileWithSmartEncoding(file);
                var report = JsonFormatValidator.Inspect(raw);

                Assert.True(report.IsParseableByNewtonsoft);
                Assert.Contains(report.Diagnostics, d => d.RuleCode == "JSON-FMT-001");
            }
        }
    }
}
