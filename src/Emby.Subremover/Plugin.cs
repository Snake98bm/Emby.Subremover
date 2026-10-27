using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Subremover.Core;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;

namespace Emby.Subremover
{
    public class Plugin : BasePluginSimpleUI<PluginOptions>, IHasThumbImage
    {
        private readonly ILibraryManager _libraryManager;

        public Plugin(IApplicationHost applicationHost, ILibraryManager libraryManager) : base(applicationHost)
        {
            _libraryManager = libraryManager;
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        public override string Name => "Subremover";

        public override string Description => "Removes unwanted embedded subtitle tracks from video files, with test mode and backups.";

        public override Guid Id => new Guid("82033f83-61d4-48e1-b20a-831041323b14");

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage() => GetType().Assembly.GetManifestResourceStream("Emby.Subremover.thumb.png");

        public PluginOptions Options => GetOptions();

        public void Save(PluginOptions options) => SaveOptions(options);

        protected override PluginOptions OnBeforeShowUI(PluginOptions options)
        {
            options.LibraryChoices = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType != "music" && f.CollectionType != "books" && f.CollectionType != "photos")
                .Select(f => new EditorSelectOption { Value = f.ItemId, Name = f.Name, IsEnabled = true })
                .ToList();

            options.LanguageChoices = LanguageChoicesFor(options.KeepLanguages);
            return base.OnBeforeShowUI(options);
        }

        protected override bool OnOptionsSaving(PluginOptions options)
        {
            if (options.ApplyEnglishPreset)
            {
                options.KeepLanguages = "eng";
                options.KeepUndefined = true;
                options.ApplyEnglishPreset = false;
            }
            return base.OnOptionsSaving(options);
        }

        // Codes saved earlier that aren't in the standard list stay selectable, so saving never drops them.
        internal static List<EditorSelectOption> LanguageChoicesFor(string selected)
        {
            var standard = Languages.All.Select(l => new EditorSelectOption { Value = l.Code, Name = $"{l.Name} ({l.Code})", IsEnabled = true });
            var extra = PlannerSettings.ParseLanguages(selected)
                .Where(code => Languages.All.All(l => l.Code != code))
                .Select(code => new EditorSelectOption { Value = code, Name = code, IsEnabled = true });
            return standard.Concat(extra).ToList();
        }
    }
}
