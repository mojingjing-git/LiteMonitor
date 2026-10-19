using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    public class MetricUtilsGpuKeyTests
    {
        [Theory]
        [InlineData("GPU.0.Temp", MetricType.Temperature)]
        [InlineData("GPU.1.Temp", MetricType.Temperature)]
        [InlineData("GPU.1.VRAM", MetricType.Memory)]
        [InlineData("GPU.1.Load", MetricType.Percent)]
        [InlineData("GPU.1.Power", MetricType.Power)]
        [InlineData("GPU.1.Clock", MetricType.Frequency)]
        [InlineData("GPU.1.Fan", MetricType.RPM)]
        public void GetType_带序号的Key_类型判定不变(string key, MetricType expected)
        {
            Assert.Equal(expected, MetricUtils.GetType(key));
        }

        [Fact]
        public void GetAdaptivePercentage_带序号的Key_使用该卡自己的峰值()
        {
            var cfg = Settings.Load();
            cfg.RecordedMaxGpuDynamic["GPU.1.Power"] = 200f;

            double pct = MetricUtils.GetAdaptivePercentage("GPU.1.Power", 100.0);
            Assert.Equal(0.5, pct, 3);

            // 另一张卡没有单独峰值时, 走全局 RecordedMaxGpuPower
            cfg.RecordedMaxGpuDynamic.Remove("GPU.2.Power");
            cfg.RecordedMaxGpuPower = 400f;
            double pct2 = MetricUtils.GetAdaptivePercentage("GPU.2.Power", 100.0);
            Assert.Equal(0.25, pct2, 3);
        }
    }
}