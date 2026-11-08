using System;
using System.Data.SQLite;
using System.Windows;
using System.Windows.Media;
using OfflineStore.Security;
using OfflineStore.Infrastructure;

namespace OfflineStore.Appearance
{
    public sealed class AppearanceSettings
    {
        public string Theme { get; set; }
        public string Accent { get; set; }
    }

    public sealed class AppearanceService
    {
        private readonly StoreDb _db;
        private readonly UserSession _actor;
        public AppearanceService(StoreDb db, UserSession actor = null) { _db = db; _actor = actor; }

        private static void Validate(string theme, string accent)
        {
            if (theme != "light" && theme != "dark") throw new ArgumentException("وضع العرض غير صالح.");
            if (accent != "blue" && accent != "green" && accent != "purple" && accent != "slate")
                throw new ArgumentException("لون الواجهة غير صالح.");
        }

        public AppearanceSettings Load()
        {
            var result = new AppearanceSettings { Theme = "light", Accent = "blue" };
            using (var c = _db.Open())
            using (var command = new SQLiteCommand("SELECT setting_key, setting_value FROM app_settings;", c))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    if (key == "theme") result.Theme = reader.GetString(1);
                    if (key == "accent") result.Accent = reader.GetString(1);
                }
            }
            Validate(result.Theme, result.Accent);
            return result;
        }

        public void Save(string theme, string accent)
        {
            if (!new UserAuthService(_db).Authorize(_actor, "APPEARANCE"))
                throw new UnauthorizedAccessException("ليست لديك صلاحية تغيير المظهر.");
            Validate(theme, accent);
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            using (var cmd = new SQLiteCommand(
                "INSERT OR REPLACE INTO app_settings(setting_key,setting_value) VALUES('theme',@theme),('accent',@accent);", c, tx))
            {
                cmd.Parameters.AddWithValue("@theme", theme);
                cmd.Parameters.AddWithValue("@accent", accent);
                cmd.ExecuteNonQuery();
                using (var audit = new SQLiteCommand(
                    "INSERT INTO audit_log(action,entity_id,details,created_utc) VALUES('APPEARANCE_UPDATED',0,'',@date)", c, tx))
                {
                    audit.Parameters.AddWithValue("@date", DateTime.UtcNow.ToString("o"));
                    audit.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
    }

    public static class ThemeManager
    {
        private static SolidColorBrush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static LinearGradientBrush Gradient(string from, string to)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            brush.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString(from), 0));
            brush.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString(to), 1));
            brush.Freeze();
            return brush;
        }

        public static void Apply(AppearanceSettings settings)
        {
            var dark = settings.Theme == "dark";
            var accent = settings.Accent == "green" ? "#078B72"
                       : settings.Accent == "purple" ? "#7058CB"
                       : settings.Accent == "slate" ? "#435F8B"
                       : "#0866DF";
            var accentLight = settings.Accent == "green" ? "#1BB79B"
                       : settings.Accent == "purple" ? "#9B86F3"
                       : settings.Accent == "slate" ? "#6D91BB"
                       : "#1598FF";
            var resources = Application.Current.Resources;
            resources["PrimaryBrush"] = Brush(accent);
            resources["PrimaryGradientBrush"] = Gradient(accentLight, accent);
            resources["HeaderBrush"] = Brush(dark ? "#061428" : "#092B57");
            resources["HeaderTextBrush"] = Brush("#FFFFFF");
            resources["HeaderSubTextBrush"] = Brush(dark ? "#C7D8EF" : "#D7E8FF");
            resources["BaseBrush"] = Brush(dark ? "#0A172A" : "#E9F2FF");
            resources["SurfaceBrush"] = Brush(dark ? "#182B43" : "#FFFFFF");
            resources["SurfaceAltBrush"] = Brush(dark ? "#213752" : "#F1F6FF");
            resources["TextBrush"] = Brush(dark ? "#EEF5FF" : "#102648");
            resources["SecondaryTextBrush"] = Brush(dark ? "#AAC0DC" : "#587295");
            resources["BorderBrush"] = Brush(dark ? "#38516F" : "#C9DDF5");
            resources["GridAltBrush"] = Brush(dark ? "#20344D" : "#F3F8FF");
            resources["NavRailBrush"] = Brush(dark ? "#122946" : "#E1EEFF");
            resources["SelectedRowBrush"] = Brush(dark ? "#284B72" : "#D6E9FF");
            resources["PositiveBrush"] = Brush(dark ? "#1EAD82" : "#087A58");
            resources["WarningBrush"] = Brush(dark ? "#E9B15E" : "#B96714");
            resources["DangerBrush"] = Brush(dark ? "#F07887" : "#B72F40");
            resources["BrandPanelBrush"] = Gradient(
                dark ? "#031125" : "#02234F", dark ? "#0C3D74" : "#08569C");
            resources["BlueMetricBrush"] = Gradient(dark ? "#1464B5" : "#187DE8",
                                                    dark ? "#0A356D" : "#0851B2");
            resources["GreenMetricBrush"] = Gradient(dark ? "#0B806B" : "#11A481",
                                                     dark ? "#06553D" : "#066F57");
            resources["PurpleMetricBrush"] = Gradient(dark ? "#6D5CB8" : "#7868D8",
                                                      dark ? "#41377C" : "#4E45A1");
        }
    }
}
