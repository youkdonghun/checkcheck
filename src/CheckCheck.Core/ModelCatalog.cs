namespace CheckCheck.Core;

/// <summary>Official, immutable downloads; no provider account or paid API is involved.</summary>
public static class ModelCatalog
{
    public const string ModelDisplayName = "Qwen3 4B · 내 PC";
    public const string ModelFileName = "Qwen3-4B-Q4_K_M.gguf";
    public const string ModelRevision = "bc640142c66e1fdd12af0bd68f40445458f3869b";
    public const string ModelUrl = "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/" + ModelRevision + "/" + ModelFileName + "?download=true";
    public const string ModelSha256 = "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5";
    public const long ModelSize = 2497280256;
    public const string RuntimeVersion = "b10883";
    public const string VulkanUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b10883/llama-b10883-bin-win-vulkan-x64.zip";
    public const string VulkanSha256 = "5de234aca70d669ce2d3f7a0aa51cf82c7e23b177149e0aae725c6df2c341722";
    public const long VulkanSize = 31656896;
    public const string CpuUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b10883/llama-b10883-bin-win-cpu-x64.zip";
    public const string CpuSha256 = "05865586d8cc235e7ed1e975e0df4c6d02ba81cab637845aed888ad5fe4ba061";
    public const long CpuSize = 18425091;
    public static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CheckCheck");
    public static string ModelPath => Path.Combine(CacheRoot, "Models", ModelFileName);
    public static string RuntimeDirectory(bool cpu) => Path.Combine(CacheRoot, "Runtime", $"llama-{RuntimeVersion}-{(cpu ? "cpu" : "vulkan")}");
}
