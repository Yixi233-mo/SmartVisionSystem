using OpenCvSharp;
using SmartVisionSystem.Core;

namespace SmartVisionSystem
{
    /// <summary>
    /// 全局状态门面：保留 MVP 时期的调用习惯（AppState.MinArea 等），
    /// 内部统一代理到 RecipeStore.Current，避免出现"两份参数各改各的"的经典 bug。
    /// </summary>
    public static class AppState
    {
        public static Recipe Recipe => RecipeStore.Current;

        public static string CurrentProductName
        {
            get => Recipe.Name;
            set => Recipe.Name = value;
        }

        public static double MinArea
        {
            get => Recipe.MinAreaPx;
            set => Recipe.MinAreaPx = value;
        }

        public static double MaxArea
        {
            get => Recipe.MaxAreaPx;
            set => Recipe.MaxAreaPx = value;
        }

        public static double MatchThreshold
        {
            get => Recipe.MatchThreshold;
            set => Recipe.MatchThreshold = value;
        }

        public static double TargetX
        {
            get => Recipe.RobotBaseX;
            set => Recipe.RobotBaseX = value;
        }

        public static double TargetY
        {
            get => Recipe.RobotBaseY;
            set => Recipe.RobotBaseY = value;
        }

        public static Mat? TemplateImage => Recipe.Template;

        public static bool CanProduce => Recipe.HasTemplate && Recipe.Validate(out _);
    }
}
