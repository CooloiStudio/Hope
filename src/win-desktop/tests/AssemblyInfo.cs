using Xunit;

// WPF / STA 控件测试与默认并行调度不搭；整集串行避免 CI 偶发假超时。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
