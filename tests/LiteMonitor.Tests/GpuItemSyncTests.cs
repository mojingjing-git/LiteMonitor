using System.Collections.Generic;
using System.Linq;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    public class GpuItemSyncTests
    {
        private static Settings NewSettings(int count, params MonitorItemConfig[] items)
        {
            var s = new Settings { MonitoredGpuCount = count };
            s.MonitorItems = new List<MonitorItemConfig>(items);
            return s;
        }

        [Fact]
        public void Ensure_旧单卡配置_迁移成带序号形式()
        {
            var s = NewSettings(1,
                new MonitorItemConfig { Key = "CPU.Load", VisibleInPanel = true },
                new MonitorItemConfig { Key = "GPU.Load", VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "GPU.Temp", VisibleInPanel = true });

            GpuItemSync.Ensure(s);

            Assert.DoesNotContain(s.MonitorItems, x => x.Key == "GPU.Load");
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Load");
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Temp");
            // 可见性从旧项继承
            Assert.True(s.MonitorItems.First(x => x.Key == "GPU.0.Load").VisibleInTaskbar);
        }

        [Fact]
        public void Ensure_两张卡_生成两组共十二项()
        {
            var s = NewSettings(2);
            GpuItemSync.Ensure(s);

            var gpuItems = s.MonitorItems.Where(x => x.Key.StartsWith("GPU.")).ToList();
            Assert.Equal(12, gpuItems.Count);
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Load");
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.1.Load");
        }

        [Fact]
        public void Ensure_减少数量_裁掉多余序号的项()
        {
            var s = NewSettings(2);
            GpuItemSync.Ensure(s);
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.1.Temp");

            s.MonitoredGpuCount = 1;
            GpuItemSync.Ensure(s);

            Assert.DoesNotContain(s.MonitorItems, x => x.Key.StartsWith("GPU.1."));
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Temp");
        }

        [Fact]
        public void Ensure_重复调用_不产生重复项()
        {
            var s = NewSettings(2);
            GpuItemSync.Ensure(s);
            int first = s.MonitorItems.Count;
            GpuItemSync.Ensure(s);
            Assert.Equal(first, s.MonitorItems.Count);
        }

        [Fact]
        public void Ensure_保留用户已有的自定义()
        {
            var s = NewSettings(1);
            GpuItemSync.Ensure(s);

            var item = s.MonitorItems.First(x => x.Key == "GPU.0.Temp");
            item.UserLabel = "我的温度";
            item.VisibleInTaskbar = true;

            GpuItemSync.Ensure(s);

            var again = s.MonitorItems.First(x => x.Key == "GPU.0.Temp");
            Assert.Equal("我的温度", again.UserLabel);
            Assert.True(again.VisibleInTaskbar);
        }

        [Fact]
        public void Ensure_数量非法时_夹到一和上限之间()
        {
            var s = NewSettings(0);
            GpuItemSync.Ensure(s);
            Assert.Equal(6, s.MonitorItems.Count(x => x.Key.StartsWith("GPU.")));

            s.MonitoredGpuCount = 999;
            GpuItemSync.Ensure(s);
            Assert.Equal(6 * GpuKey.MaxGpuCount, s.MonitorItems.Count(x => x.Key.StartsWith("GPU.")));
        }

        [Fact]
        public void Ensure_实际卡数不足时_夹紧并回写数量()
        {
            // 机器上只有 2 张卡, 但配置里写了 8
            var s = NewSettings(8);
            GpuItemSync.Ensure(s, availableGpuCount: 2);

            // 只生成 2 组, 不会出现 6 组空白监控项
            Assert.Equal(12, s.MonitorItems.Count(x => x.Key.StartsWith("GPU.")));
            Assert.DoesNotContain(s.MonitorItems, x => x.Key.StartsWith("GPU.2."));
            // 数量被夹紧后回写, 让设置页显示真实生效值
            Assert.Equal(2, s.MonitoredGpuCount);
        }

        [Fact]
        public void Ensure_实际卡数充足时_不改动数量()
        {
            var s = NewSettings(2);
            GpuItemSync.Ensure(s, availableGpuCount: 4);
            Assert.Equal(2, s.MonitoredGpuCount);
            Assert.Equal(12, s.MonitorItems.Count(x => x.Key.StartsWith("GPU.")));
        }

        [Fact]
        public void Ensure_保留范围内的自定义带序号Key()
        {
            // 用户/插件自建的 "GPU.0.Custom" 不该被静默删掉
            // (Task 5 Step 2.5 放行了带序号的 Key, Ensure 就不该再把它删掉)
            var s = NewSettings(2, new MonitorItemConfig
            {
                Key = "GPU.0.Custom",
                UserLabel = "我的自定义",
                SortIndex = 999,
                TaskbarSortIndex = 999,
            });

            GpuItemSync.Ensure(s, availableGpuCount: 2);

            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Custom");
            var custom = s.MonitorItems.First(x => x.Key == "GPU.0.Custom");
            Assert.Equal("我的自定义", custom.UserLabel);
        }

        [Fact]
        public void Ensure_丢弃超出卡数的自定义带序号Key()
        {
            // count=2 时 "GPU.2.Custom" 属于越界项, 不能复活 ——
            // 否则又会出现计划要消灭的"空白监控项"。
            var s = NewSettings(2, new MonitorItemConfig
            {
                Key = "GPU.2.Custom",
                UserLabel = "越界",
                SortIndex = 999,
                TaskbarSortIndex = 999,
            });

            GpuItemSync.Ensure(s, availableGpuCount: 2);

            Assert.DoesNotContain(s.MonitorItems, x => x.Key.StartsWith("GPU.2."));
            // 标准项 2 组 x 6 = 12, 越界的自定义项不计入
            Assert.Equal(12, s.MonitorItems.Count(x => x.Key.StartsWith("GPU.")));
        }

    }
}