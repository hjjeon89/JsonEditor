using System;
using System.IO;
using System.Linq;
using System.Text;
using JsonEditor.Models;
using JsonEditor.ViewModels;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JsonEditor.Tests
{
    public class ArbitraryJsonStructureTests
    {
        [Fact]
        public void RootArrayJson_ShouldLoad_Edit_CloneItem_AndRoundTripAsRootArray()
        {
            string rootArrayJson = @"[
  {
    ""StepNo"": 1,
    ""Mode"": ""Charge"",
    ""Voltage"": 4.2,
    ""Enabled"": true
  },
  {
    ""StepNo"": 2,
    ""Mode"": ""Discharge"",
    ""Voltage"": 2.8,
    ""Enabled"": false
  }
]";
            JToken parsed = JToken.Parse(rootArrayJson);
            var rootNode = new JsonTreeNode("Root", parsed, null, () => { });

            Assert.Equal(JsonNodeKind.Array, rootNode.NodeKind);
            Assert.Equal(2, rootNode.Children.Count);

            // 배열 원소 복제 추가 실행 -> 마지막 원소([1])의 키 구조(StepNo, Mode, Voltage, Enabled)를 그대로 복제한 [2]가 생성되어야 함
            rootNode.AddArrayItemCommand.Execute(null);
            Assert.Equal(3, rootNode.Children.Count);
            Assert.True(rootNode.IsModified);
            Assert.Equal("[2]", rootNode.Children[2].Key);
            Assert.Equal(4, rootNode.Children[2].Children.Count);

            JToken output = rootNode.ToJToken();
            Assert.Equal(JTokenType.Array, output.Type);
            Assert.Equal(3, ((JArray)output).Count);

            // Revert 시 다시 2개 원소로 복원
            rootNode.RevertCommand.Execute(null);
            Assert.False(rootNode.IsModified);
            Assert.Equal(2, rootNode.Children.Count);
        }

        [Fact]
        public void DeeplyNestedObject_AndPrimitiveArrays_ShouldRoundTrip_AndTrackModification()
        {
            string deepJson = @"{
  ""System"": {
    ""Network"": {
      ""Endpoints"": [
        {
          ""Host"": ""192.168.0.10"",
          ""Ports"": [ 1530, 2001, 502 ],
          ""Metadata"": {
            ""Timeout_ms"": 5000,
            ""RetryRatio"": 0.125,
            ""NullableNote"": null
          }
        }
      ]
    }
  }
}";
            JToken orig = JToken.Parse(deepJson);
            var root = new JsonTreeNode("Root", orig, null, () => { });

            Assert.False(root.IsModified);
            Assert.True(JToken.DeepEquals(orig, root.ToJToken()));

            // 깊은 경로의 Timeout_ms 노드 찾기
            var timeoutNode = root.Children[0] // System
                .Children[0] // Network
                .Children[0] // Endpoints
                .Children[0] // [0]
                .Children[2] // Metadata
                .Children[0]; // Timeout_ms

            Assert.Equal("$.System.Network.Endpoints[0].Metadata.Timeout_ms", timeoutNode.JsonPath);
            Assert.Equal(JsonNodeKind.Integer, timeoutNode.NodeKind);

            // 잘못된 문자열 입력 시 ValidationError 발생 검증
            timeoutNode.StringValue = "invalid_num";
            Assert.True(timeoutNode.HasError);
            Assert.True(root.HasError);

            // 정상 정수 입력 시 에러 해제 및 상위 노드까지 IsModified 전파 검증
            timeoutNode.StringValue = "10000";
            Assert.False(timeoutNode.HasError);
            Assert.False(root.HasError);
            Assert.True(timeoutNode.IsModified);
            Assert.True(root.IsModified);

            JToken updated = root.ToJToken();
            Assert.Equal(10000, updated["System"]?["Network"]?["Endpoints"]?[0]?["Metadata"]?["Timeout_ms"]?.Value<int>());

            // 원래 값(5000)으로 되돌리면 IsModified가 자동으로 false가 되어야 함
            timeoutNode.StringValue = "5000";
            Assert.False(timeoutNode.IsModified);
            Assert.False(root.IsModified);
        }

        [Fact]
        public void ObjectProperty_Add_PreventDuplicateKey_Move_And_Delete_ShouldWorkCorrectly()
        {
            JToken orig = JToken.Parse(@"{ ""First"": 1, ""Second"": 2 }");
            var root = new JsonTreeNode("Root", orig, null, () => { });

            // 중복 키 추가 시도 -> 거부되어야 함
            var dupResult = root.AddObjectProperty("First", "Integer", "99", out string? dupErr);
            Assert.Null(dupResult);
            Assert.NotNull(dupErr);

            // 새 속성 추가
            var added = root.AddObjectProperty("ThirdPath", "String", @"C:\GreenPower\Test.json", out string? addErr);
            Assert.Null(addErr);
            Assert.NotNull(added);
            Assert.True(added!.IsFilePathLike);

            // 위로 이동
            added.MoveUpCommand.Execute(null);
            Assert.Equal(new[] { "First", "ThirdPath", "Second" }, root.Children.Select(c => c.Key).ToArray());

            // 직렬화 시 역슬래시가 자동 이스케이프되어 유효한 JSON이 생성되는지 검증
            string serialized = root.ToJToken().ToString();
            Assert.Contains(@"C:\\GreenPower\\Test.json", serialized);

            // 삭제
            added.DeleteSelfCommand.Execute(null);
            Assert.Equal(new[] { "First", "Second" }, root.Children.Select(c => c.Key).ToArray());
        }

        [Fact]
        public void LegacyCp949EucKrEncodedJsonFile_ShouldBeDecodedWithoutBrokenKoreanText()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding cp949 = Encoding.GetEncoding(949);

            string koreanJson = @"{ ""Name"": ""타워램프 적색등 점등"", ""Description"": ""R상 순시 과전류 알람"" }";
            string tempFile = Path.Combine(Path.GetTempPath(), $"cp949_test_{Guid.NewGuid():N}.json");

            try
            {
                File.WriteAllBytes(tempFile, cp949.GetBytes(koreanJson));

                string readBack = MainViewModel.ReadFileWithSmartEncoding(tempFile);
                Assert.Contains("타워램프 적색등 점등", readBack);
                Assert.Contains("R상 순시 과전류 알람", readBack);

                var vm = new MainViewModel();
                vm.LoadJsonFile(tempFile);
                Assert.Equal(0, vm.ErrorCount);
                Assert.Equal("타워램프 적색등 점등", vm.RootNodes[0].Children[0].StringValue);
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }
    }
}
