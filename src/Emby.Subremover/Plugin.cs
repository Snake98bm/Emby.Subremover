using System;
using System.Linq;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;

namespace Emby.Subremover
{
    public class Plugin : BasePluginSimpleUI<PluginOptions>
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

        public PluginOptions Options => GetOptions();

        public void Save(PluginOptions options) => SaveOptions(options);

        protected override PluginOptions OnBeforeShowUI(PluginOptions options)
        {
            options.LibraryChoices = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType != "music" && f.CollectionType != "books" && f.CollectionType != "photos")
                .Select(f => new EditorSelectOption { Value = f.ItemId, Name = f.Name, IsEnabled = true })
                .ToList();
            return base.OnBeforeShowUI(options);
        }
    }
}
