using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;
using SmartVisionSystem.Infrastructure;

namespace SmartVisionSystem.Core
{
    /// <summary>
    /// 配方（换线核心数据）：一张配方 = 一个产品的模板 + 判定标准 + 机械手基准点 + 相机参数。
    /// 落盘为 JSON + 模板 PNG，可随工程整体拷贝，符合产线"一键换线"诉求。
    /// </summary>
    public sealed class Recipe : IDisposable
    {
        // ── 基本信息 ──
        public string Name { get; set; } = "产品-A";
        public string Description { get; set; } = "";
        public string UpdatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        // ── 判定标准 ──
        /// <summary>材料面积下限（px²，已扣除内孔）</summary>
        public double MinAreaPx { get; set; } = 3000;
        /// <summary>材料面积上限（px²）</summary>
        public double MaxAreaPx { get; set; } = 8000;
        /// <summary>模板匹配最低得分 0~1</summary>
        public double MatchThreshold { get; set; } = 0.80;
        /// <summary>表面缺陷像素占比上限（划伤/污点），0.006 = 0.6%</summary>
        public double MaxSurfaceDefectRatio { get; set; } = 0.006;

        // ── 匹配搜索策略 ──
        /// <summary>搜索窗口半径（px），以基准点为中心的搜索范围</summary>
        public int SearchWindowPx { get; set; } = 90;
        /// <summary>旋转搜索范围 ±deg</summary>
        public double SearchAngleDeg { get; set; } = 8;
        /// <summary>旋转搜索步距 deg</summary>
        public double SearchAngleStep { get; set; } = 2;

        // ── 标定与基准 ──
        /// <summary>像素当量：多少像素 = 1mm</summary>
        public double PixelsPerMm { get; set; } = 2.0;
        /// <summary>模板建立时，工件中心在图像中的坐标（视觉基准）</summary>
        public double ImageBaseX { get; set; } = 640;
        public double ImageBaseY { get; set; } = 480;
        /// <summary>机械手取料基准坐标（图纸标称位置，mm）</summary>
        public double RobotBaseX { get; set; } = 250;
        public double RobotBaseY { get; set; } = 250;

        // ── 机器人动作点位（mm） ──
        public double ZSafe { get; set; } = 80;
        public double ZPick { get; set; } = 5;
        public double ZPlace { get; set; } = 10;
        public double PlaceX { get; set; } = 420;
        public double PlaceY { get; set; } = 160;
        /// <summary>NG 料道放料点（不良品同样要取走，避免产线堵塞）</summary>
        public double PlaceNgX { get; set; } = 420;
        public double PlaceNgY { get; set; } = 380;

        // ── 相机参数 ──
        public double ExposureUs { get; set; } = 8000;
        public double GainDb { get; set; } = 0;

        // ── 节拍 ──
        /// <summary>节拍上限（ms），超过则告警</summary>
        public double MaxCycleMs { get; set; } = 3500;

        // ── 模板文件 ──
        public string TemplateFile { get; set; } = "";

        /// <summary>是否已具备可生产的模板</summary>
        [JsonIgnore]
        public bool HasTemplate => Template != null && !Template.Empty();

        [JsonIgnore] public Mat? Template { get; set; }
        [JsonIgnore] public Mat? TemplateGray { get; set; }

        public Recipe Clone()
        {
            var r = (Recipe)MemberwiseClone();
            r.Template = null;
            r.TemplateGray = null;
            return r;
        }

        /// <summary>深拷贝（含模板图像），供"另存为"使用</summary>
        public Recipe CloneWithTemplate()
        {
            var r = Clone();
            if (Template != null) r.Template = Template.Clone();
            if (TemplateGray != null) r.TemplateGray = TemplateGray.Clone();
            return r;
        }

        public bool Validate(out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(Name)) { error = "配方名称不能为空"; return false; }
            if (!HasTemplate) { error = "尚未建立视觉模板，请先在【参数与图纸】中框选并保存模板"; return false; }
            if (MatchThreshold is < 0.1 or > 0.99) { error = "匹配得分阈值应在 0.1 ~ 0.99 之间"; return false; }
            if (MinAreaPx >= MaxAreaPx) { error = "最小面积必须小于最大面积"; return false; }
            if (PixelsPerMm is < 0.1 or > 100) { error = "像素当量异常（0.1~100 px/mm）"; return false; }
            if (ZPick >= ZSafe) { error = "取料 Z 必须低于安全 Z（避免撞机）"; return false; }
            return true;
        }

        public void Dispose()
        {
            Template?.Dispose(); Template = null;
            TemplateGray?.Dispose(); TemplateGray = null;
        }
    }

    /// <summary>
    /// 配方仓库：负责加载/保存/切换当前配方，并管理模板 Mat 的生命周期（延迟释放，避免生产线程正在使用时被回收）。
    /// </summary>
    public static class RecipeStore
    {
        private static readonly object _sync = new();
        private static readonly List<(Mat Mat, DateTime RetiredAt)> _retired = new();
        private static readonly string LastRecipeFile = Path.Combine(AppPaths.DataDir, "last_recipe.txt");

        public static Recipe Current { get; private set; } = new();
        public static event Action<Recipe>? CurrentChanged;

        static RecipeStore()
        {
            Current = new Recipe { Name = "产品-A" };
        }

        public static string PathOf(string name) => Path.Combine(AppPaths.RecipeDir, Sanitize(name) + ".json");

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        public static IReadOnlyList<string> List()
        {
            try
            {
                return Directory.GetFiles(AppPaths.RecipeDir, "*.json")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .OrderBy(n => n)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Error("配方", "枚举配方目录失败", ex);
                return Array.Empty<string>();
            }
        }

        public static Recipe? Load(string name, bool makeCurrent = true)
        {
            var file = PathOf(name);
            if (!File.Exists(file)) return null;
            try
            {
                var json = File.ReadAllText(file);
                var recipe = JsonSerializer.Deserialize<Recipe>(json, JsonOpts);
                if (recipe == null) return null;

                if (!string.IsNullOrWhiteSpace(recipe.TemplateFile))
                {
                    var path = AppPaths.Resolve(recipe.TemplateFile);
                    if (File.Exists(path))
                    {
                        var bytes = File.ReadAllBytes(path);
                        recipe.Template = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
                        if (!recipe.Template.Empty())
                            recipe.TemplateGray = recipe.Template.Clone();
                    }
                    else
                    {
                        Log.Warn("配方", $"配方 {name} 的模板文件缺失：{path}");
                    }
                }

                Log.Info("配方", $"加载配方 {recipe.Name}（阈值 {recipe.MatchThreshold:F2}，面积 {recipe.MinAreaPx:F0}~{recipe.MaxAreaPx:F0}px²）");
                if (makeCurrent) Apply(recipe);
                return recipe;
            }
            catch (Exception ex)
            {
                Log.Error("配方", $"加载配方 {name} 失败", ex);
                return null;
            }
        }

        public static bool Save(Recipe recipe)
        {
            try
            {
                if (recipe.Template != null && !recipe.Template.Empty())
                {
                    var tplName = Sanitize(recipe.Name) + ".template.png";
                    var tplPath = Path.Combine(AppPaths.RecipeDir, tplName);
                    Cv2.ImWrite(tplPath, recipe.Template);
                    recipe.TemplateFile = Path.Combine("data", "recipes", tplName);
                }

                recipe.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var json = JsonSerializer.Serialize(recipe, JsonOpts);
                File.WriteAllText(PathOf(recipe.Name), json);
                File.WriteAllText(LastRecipeFile, recipe.Name);

                Log.Info("配方", $"配方 {recipe.Name} 已保存（模板 {(recipe.HasTemplate ? $"{recipe.Template!.Width}x{recipe.Template.Height}" : "无")}）");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("配方", $"保存配方 {recipe.Name} 失败", ex);
                return false;
            }
        }

        public static bool Delete(string name)
        {
            try
            {
                var file = PathOf(name);
                if (File.Exists(file)) File.Delete(file);
                var tpl = Path.Combine(AppPaths.RecipeDir, Sanitize(name) + ".template.png");
                if (File.Exists(tpl)) File.Delete(tpl);
                Log.Info("配方", $"删除配方 {name}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("配方", $"删除配方 {name} 失败", ex);
                return false;
            }
        }

        /// <summary>把配方设为当前生产配方（换线动作）</summary>
        public static void Apply(Recipe recipe)
        {
            lock (_sync)
            {
                var old = Current;
                Current = recipe;
                if (!ReferenceEquals(old, recipe) && old != null)
                {
                    _retired.Add((old.Template!, DateTime.Now));
                    _retired.Add((old.TemplateGray!, DateTime.Now));
                }
                CleanRetired();
            }
            CurrentChanged?.Invoke(RecipeStore.Current);
        }

        private static void CleanRetired()
        {
            // 只释放"退休"超过 5 秒的模板，确保生产线程绝不会踩到已释放的 Mat
            for (int i = _retired.Count - 1; i >= 0; i--)
            {
                if ((DateTime.Now - _retired[i].RetiredAt).TotalSeconds > 5)
                {
                    try { _retired[i].Mat?.Dispose(); } catch { }
                    _retired.RemoveAt(i);
                }
            }
        }

        /// <summary>启动时恢复上次使用的配方</summary>
        public static void RestoreLastOrDefault()
        {
            try
            {
                if (File.Exists(LastRecipeFile))
                {
                    var name = File.ReadAllText(LastRecipeFile).Trim();
                    if (!string.IsNullOrWhiteSpace(name) && Load(name) != null) return;
                }

                var list = List();
                if (list.Count > 0 && Load(list[0]) != null) return;

                Log.Warn("配方", "未找到任何配方，将使用空配方（需工程师建立模板后才能启动）");
            }
            catch (Exception ex)
            {
                Log.Error("配方", "恢复上次配方失败", ex);
            }
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }
}
