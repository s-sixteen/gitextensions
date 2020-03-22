using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using GitCommands;
using GitCommands.Git;
using GitCommands.Patches;
using GitCommands.Utils;
using GitExtUtils;
using GitUI.CommandsDialogs.BrowseDialog;
using GitUI.HelperDialogs;
using GitUI.Hotkey;
using GitUI.UserControls;
using GitUI.UserControls.RevisionGrid;
using GitUIPluginInterfaces;
using JetBrains.Annotations;
using Microsoft.VisualStudio.Threading;
using ResourceManager;

namespace GitUI.CommandsDialogs
{
    public partial class RevisionDiffControl : GitModuleControl
    {
        private readonly TranslationString _saveFileFilterCurrentFormat = new TranslationString("Current format");
        private readonly TranslationString _saveFileFilterAllFiles = new TranslationString("All files");
        private readonly TranslationString _deleteSelectedFilesCaption = new TranslationString("Delete");
        private readonly TranslationString _deleteSelectedFiles =
            new TranslationString("Are you sure you want to delete the selected file(s)?");
        private readonly TranslationString _deleteFailed = new TranslationString("Delete file failed");
        private readonly TranslationString _multipleDescription = new TranslationString("<multiple>");
        private readonly TranslationString _selectedRevision = new TranslationString("Selected: b/");
        private readonly TranslationString _firstRevision = new TranslationString("First: a/");

        private RevisionGridControl _revisionGrid;
        private RevisionFileTreeControl _revisionFileTree;
        private IRevisionDiffController _revisionDiffController;
        private readonly IFileStatusListContextMenuController _revisionDiffContextMenuController;
        private readonly IFullPathResolver _fullPathResolver;
        private readonly IFindFilePredicateProvider _findFilePredicateProvider;
        private readonly IGitRevisionTester _gitRevisionTester;
        private bool _selectedDiffReloaded = true;

        public RevisionDiffControl()
        {
            InitializeComponent();
            DiffFiles.GroupByRevision = true;
            InitializeComplete();
            HotkeysEnabled = true;
            _fullPathResolver = new FullPathResolver(() => Module.WorkingDir);
            _findFilePredicateProvider = new FindFilePredicateProvider();
            _gitRevisionTester = new GitRevisionTester(_fullPathResolver);
            _revisionDiffContextMenuController = new FileStatusListContextMenuController();
            DiffText.CherryPickContextMenuEntry_OverrideClick(StageSelectedLinesToolStripMenuItemClick, ResetSelectedLinesToolStripMenuItemClick);
        }

        public void RefreshArtificial()
        {
            if (!Visible)
            {
                return;
            }

            var revisions = _revisionGrid.GetSelectedRevisions();
            if (!revisions.Any(r => r.IsArtificial))
            {
                return;
            }

            DiffFiles.StoreNextIndexToSelect();
            SetDiffs(revisions);
            if (DiffFiles.SelectedItemWithParent == null)
            {
                DiffFiles.SelectStoredNextIndex();
            }
        }

        #region Hotkey commands

        public static readonly string HotkeySettingsName = "BrowseDiff";

        public enum Command
        {
            DeleteSelectedFiles = 0,
            ShowHistory = 1,
            Blame = 2,
            OpenWithDifftool = 3,
            EditFile = 4,
            OpenAsTempFile = 5,
            OpenAsTempFileWith = 6,
            OpenWithDifftoolFirstToLocal = 7,
            OpenWithDifftoolSelectedToLocal = 8,
            ResetSelectedFiles = 9,
            StageSelectedFile = 10,
            UnStageSelectedFile = 11,
        }

        public CommandStatus ExecuteCommand(Command cmd)
        {
            return ExecuteCommand((int)cmd);
        }

        protected override CommandStatus ExecuteCommand(int cmd)
        {
            if (DiffFiles.FilterFocused && IsTextEditKey(GetShortcutKeys(cmd)))
            {
                return false;
            }

            switch ((Command)cmd)
            {
                case Command.DeleteSelectedFiles: return DeleteSelectedFiles();
                case Command.ShowHistory: fileHistoryDiffToolstripMenuItem.PerformClick(); break;
                case Command.Blame: blameToolStripMenuItem.PerformClick(); break;
                case Command.OpenWithDifftool: firstToSelectedToolStripMenuItem.PerformClick(); break;
                case Command.OpenWithDifftoolFirstToLocal: firstToLocalToolStripMenuItem.PerformClick(); break;
                case Command.OpenWithDifftoolSelectedToLocal: selectedToLocalToolStripMenuItem.PerformClick(); break;
                case Command.EditFile: diffEditWorkingDirectoryFileToolStripMenuItem.PerformClick(); break;
                case Command.OpenAsTempFile: diffOpenRevisionFileToolStripMenuItem.PerformClick(); break;
                case Command.OpenAsTempFileWith: diffOpenRevisionFileWithToolStripMenuItem.PerformClick(); break;
                case Command.ResetSelectedFiles: return ResetSelectedFiles();
                case Command.StageSelectedFile: return StageSelectedFile();
                case Command.UnStageSelectedFile: return UnStageSelectedFile();

                default: return base.ExecuteCommand(cmd);
            }

            return true;
        }

        public void ReloadHotkeys()
        {
            Hotkeys = HotkeySettingsManager.LoadHotkeys(HotkeySettingsName);
            diffDeleteFileToolStripMenuItem.ShortcutKeyDisplayString = GetShortcutKeyDisplayString(Command.DeleteSelectedFiles);
            fileHistoryDiffToolstripMenuItem.ShortcutKeyDisplayString = GetShortcutKeyDisplayString(Command.ShowHistory);
            blameToolStripMenuItem.ShortcutKeyDisplayString = GetShortcutKeyDisplayString(Command.Blame);
            firstToSelectedToolStripMenuItem.ShortcutKeyDisplayString = GetShortcutKeyDisplayString(Command.OpenWithDifftool);
            firstToLocalToolStripMenuItem.ShortcutKeyDisplayString = GetShortcutKeyDisplayString(Command.OpenWithDifftoolFirstToLocal);
            selectedToLocalToolStripMenuItem.ShortcutKeyDisplayString = GetShortcutKeyDisplayString(Command.OpenWithDifftoolSelectedToLocal);
            diffEditWorkingDirectoryFileToolStripMenuItem.ShortcutKeyDisplayString = GetShortcutKeyDisplayString(Command.EditFile);
            diffOpenRevisionFileToolStripMenuItem.ShortcutKeyDisplayString = GetShortcutKeyDisplayString(Command.OpenAsTempFile);
            diffOpenRevisionFileWithToolStripMenuItem.ShortcutKeyDisplayString = GetShortcutKeyDisplayString(Command.OpenAsTempFileWith);
            resetFileToParentToolStripMenuItem.ShortcutKeyDisplayString = GetShortcutKeyDisplayString(Command.ResetSelectedFiles);
            stageFileToolStripMenuItem.ShortcutKeyDisplayString = GetShortcutKeyDisplayString(Command.StageSelectedFile);
            unstageFileToolStripMenuItem.ShortcutKeyDisplayString = GetShortcutKeyDisplayString(Command.UnStageSelectedFile);

            DiffText.ReloadHotkeys();
        }

        private string GetShortcutKeyDisplayString(Command cmd)
        {
            return GetShortcutKeys((int)cmd).ToShortcutKeyDisplayString();
        }

        #endregion

        public void DisplayDiffTab()
        {
            var revisions = _revisionGrid.GetSelectedRevisions();
            SetDiffs(revisions);
            if (DiffFiles.SelectedItemWithParent == null)
            {
                DiffFiles.SelectFirstVisibleItem();
            }
        }

        private void SetDiffs(IReadOnlyList<GitRevision> revisions)
        {
            var oldDiffItem = DiffFiles.SelectedItemWithParent;
            DiffFiles.SetDiffs(revisions, _revisionGrid.GetRevision);

            // Try to restore previous item
            if (oldDiffItem != null && DiffFiles.GitItemStatuses.Any(i => i.Name.Equals(oldDiffItem.Item.Name)))
            {
                DiffFiles.SelectedItem = oldDiffItem.Item;
            }
        }

        public void Bind(RevisionGridControl revisionGrid, RevisionFileTreeControl revisionFileTree)
        {
            _revisionGrid = revisionGrid;
            _revisionFileTree = revisionFileTree;
        }

        public void InitSplitterManager(SplitterManager splitterManager)
        {
            splitterManager.AddSplitter(DiffSplitContainer, "DiffSplitContainer");
        }

        protected override void OnRuntimeLoad()
        {
            _revisionDiffController = new RevisionDiffController(_gitRevisionTester);

            DiffFiles.DescribeRevision = objectId => DescribeRevision(objectId);
            DiffText.SetFileLoader(GetNextPatchFile);
            DiffText.Font = AppSettings.FixedWidthFont;
            ReloadHotkeys();

            base.OnRuntimeLoad();
        }

        private string DescribeRevision([CanBeNull] ObjectId objectId, int maxLength = 0)
        {
            if (objectId == null)
            {
                // No parent at all, present as working directory
                return ResourceManager.Strings.Workspace;
            }

            var revision = _revisionGrid.GetRevision(objectId);

            if (revision == null)
            {
                return objectId.ToShortString();
            }

            return _revisionGrid.DescribeRevision(revision, maxLength);
        }

        /// <summary>
        /// Provide a description for the first selected or parent to the "primary" selected last
        /// </summary>
        /// <returns>A description of the selected parent</returns>
        [CanBeNull]
        private string DescribeRevision(List<GitRevision> parents)
        {
            if (parents.Count == 1)
            {
                return DescribeRevision(parents.FirstOrDefault()?.ObjectId, 50);
            }

            if (parents.Count > 1)
            {
                return _multipleDescription.Text;
            }

            return null;
        }

        private bool GetNextPatchFile(bool searchBackward, bool loop, out int fileIndex, out Task loadFileContent)
        {
            fileIndex = -1;
            loadFileContent = Task.CompletedTask;
            if (DiffFiles.SelectedItemWithParent == null)
            {
                return false;
            }

            int idx = DiffFiles.SelectedIndex;
            if (idx == -1)
            {
                return false;
            }

            fileIndex = DiffFiles.GetNextIndex(searchBackward, loop);
            if (fileIndex == idx)
            {
                if (!loop)
                {
                    return false;
                }
            }
            else
            {
                DiffFiles.SetSelectedIndex(fileIndex, notify: false);
            }

            loadFileContent = ShowSelectedFileDiffAsync();
            return true;
        }

        private ContextMenuSelectionInfo GetSelectionInfo()
        {
            // Some items are not supported if more than one revision is selected
            var revisions = DiffFiles.SelectedItemsWithParent.Select(item => item.SelectedRevision).Distinct().ToList();
            var selectedRev = revisions.Count() != 1 ? null : revisions.FirstOrDefault();

            // First (A) is parent if one revision selected or if parent, then selected
            var parentIds = DiffFiles.SelectedItemsWithParent.Select(i => i.ParentRevision.ObjectId).Distinct().ToList();

            // Combined diff is a display only diff, no manipulations
            bool isAnyCombinedDiff = parentIds.Contains(ObjectId.CombinedDiffId);
            bool isExactlyOneItemSelected = DiffFiles.SelectedItemsWithParent.Count() == 1;
            bool isAnyItemSelected = DiffFiles.SelectedItemsWithParent.Any();

            // No changes to files in bare repos
            bool isBareRepository = Module.IsBareRepository();
            bool isAnyTracked = DiffFiles.SelectedItemsWithParent.Any(item => item.Item.IsTracked);
            bool isAnyIndex = DiffFiles.SelectedItemsWithParent.Any(item => item.Item.Staged == StagedStatus.Index);
            bool isAnyWorkTree = DiffFiles.SelectedItemsWithParent.Any(item => item.Item.Staged == StagedStatus.WorkTree);
            bool isAnySubmodule = DiffFiles.SelectedItemsWithParent.Any(item => item.Item.IsSubmodule);
            bool singleFileExists = isExactlyOneItemSelected && File.Exists(_fullPathResolver.Resolve(DiffFiles.SelectedItemWithParent.Item.Name));

            var selectionInfo = new ContextMenuSelectionInfo(
                selectedRevision: selectedRev,
                isAnyCombinedDiff: isAnyCombinedDiff,
                isSingleGitItemSelected: isExactlyOneItemSelected,
                isAnyItemSelected: isAnyItemSelected,
                isAnyItemIndex: isAnyIndex,
                isAnyItemWorkTree: isAnyWorkTree,
                isBareRepository: isBareRepository,
                singleFileExists: singleFileExists,
                isAnyTracked: isAnyTracked,
                isAnySubmodule: isAnySubmodule);
            return selectionInfo;
        }

        private void ResetSelectedItemsTo(bool actsAsChild)
        {
            var selectedItems = DiffFiles.SelectedItemsWithParent.ToList();
            if (!selectedItems.Any())
            {
                return;
            }

            if (actsAsChild)
            {
                // selected revisions
                var deletedItems = selectedItems
                    .Where(item => item.Item.IsDeleted)
                    .Select(item => item.Item.Name).ToList();
                Module.RemoveFiles(deletedItems, false);

                foreach (var childId in selectedItems.Select(i => i.SelectedRevision.ObjectId).Distinct())
                {
                    var itemsToCheckout = selectedItems
                        .Where(item => !item.Item.IsDeleted && item.SelectedRevision.ObjectId == childId)
                        .Select(item => item.Item.Name).ToList();
                    Module.CheckoutFiles(itemsToCheckout, childId, force: false);
                }
            }
            else
            {
                // acts as parent
                // if file is new to the parent or is copied, it has to be removed
                var addedItems = selectedItems
                    .Where(item => item.Item.IsNew || item.Item.IsCopied)
                    .Select(item => item.Item.Name).ToList();
                Module.RemoveFiles(addedItems, false);

                foreach (var parentId in selectedItems.Select(i => i.ParentRevision.ObjectId).Distinct())
                {
                    var itemsToCheckout = selectedItems
                        .Where(item => !item.Item.IsNew && item.ParentRevision.ObjectId == parentId)
                        .Select(item => item.Item.Name).ToList();
                    Module.CheckoutFiles(itemsToCheckout, parentId, force: false);
                }
            }

            RefreshArtificial();
        }

        private async Task ShowSelectedFileDiffAsync()
        {
            GitItemStatusWithParent item = DiffFiles.SelectedItemWithParent;
            if (item?.SelectedRevision == null || item.Item == null)
            {
                DiffText.Clear();
                return;
            }

            if (item.ParentRevision?.ObjectId == ObjectId.CombinedDiffId)
            {
                var diffOfConflict = Module.GetCombinedDiffContent(item.SelectedRevision, item.Item.Name,
                    DiffText.GetExtraDiffArguments(), DiffText.Encoding);

                if (string.IsNullOrWhiteSpace(diffOfConflict))
                {
                    diffOfConflict = Strings.UninterestingDiffOmitted;
                }

                await DiffText.ViewPatchAsync(item.Item.Name,
                    text: diffOfConflict,
                    openWithDifftool: () => firstToSelectedToolStripMenuItem.PerformClick(),
                    isText: item.Item.IsSubmodule);

                return;
            }

            await DiffText.ViewChangesAsync(item.ParentRevision?.ObjectId, item.SelectedRevision, item.Item, string.Empty,
                openWithDifftool: () => firstToSelectedToolStripMenuItem.PerformClick());
        }

        private void DiffFiles_SelectedIndexChanged(object sender, EventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ShowSelectedFileDiffAsync();
            }).FileAndForget();
        }

        private void DiffFiles_DoubleClick(object sender, EventArgs e)
        {
            GitItemStatusWithParent item = DiffFiles.SelectedItemWithParent;
            if (item?.SelectedRevision == null || item.Item == null || !item.Item.IsTracked)
            {
                return;
            }

            if (AppSettings.OpenSubmoduleDiffInSeparateWindow && item.Item.IsSubmodule)
            {
                var submoduleName = item.Item.Name;

                ThreadHelper.JoinableTaskFactory.RunAsync(
                    async () =>
                    {
                        var status = await item.Item.GetSubmoduleStatusAsync().ConfigureAwait(false);

                        var process = new Process
                        {
                            StartInfo =
                            {
                                FileName = Application.ExecutablePath,
                                Arguments = "browse -commit=" + status.Commit,
                                WorkingDirectory = _fullPathResolver.Resolve(submoduleName.EnsureTrailingPathSeparator())
                            }
                        };

                        process.Start();
                    });
            }
            else
            {
                UICommands.StartFileHistoryDialog(this, item.Item.Name, item.SelectedRevision);
            }
        }

        private void DiffFiles_DataSourceChanged(object sender, EventArgs e)
        {
            if (DiffFiles.GitItemStatuses == null || !DiffFiles.GitItemStatuses.Any())
            {
                DiffText.Clear();
            }
        }

        private void DiffText_ExtraDiffArgumentsChanged(object sender, EventArgs e)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ShowSelectedFileDiffAsync();
            }).FileAndForget();
        }

        private void diffShowInFileTreeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // switch to view (and fills the first level of file tree data model if not already done)
            (FindForm() as FormBrowse)?.ExecuteCommand(FormBrowse.Command.FocusFileTree);
            _revisionFileTree.ExpandToFile(DiffFiles.SelectedItemsWithParent.First().Item.Name);
        }

        private void DiffContextMenu_Opening(object sender, CancelEventArgs e)
        {
            var selectionInfo = GetSelectionInfo();

            // Many options have no meaning for artificial commits or submodules
            // Hide the obviously no action options when single selected, handle them in actions if multi select

            openWithDifftoolToolStripMenuItem.Enabled = _revisionDiffController.ShouldShowDifftoolMenus(selectionInfo);
            saveAsToolStripMenuItem1.Visible = _revisionDiffController.ShouldShowMenuSaveAs(selectionInfo);
            copyFilenameToClipboardToolStripMenuItem1.Enabled = _revisionDiffController.ShouldShowMenuCopyFileName(selectionInfo);

            stageFileToolStripMenuItem.Visible = _revisionDiffController.ShouldShowMenuStage(selectionInfo);
            unstageFileToolStripMenuItem.Visible = _revisionDiffController.ShouldShowMenuUnstage(selectionInfo);

            cherryPickSelectedDiffFileToolStripMenuItem.Visible = _revisionDiffController.ShouldShowMenuCherryPick(selectionInfo);

            // Visibility of FileTree is not known, assume (CommitInfoTabControl.Contains(TreeTabPage);)
            diffShowInFileTreeToolStripMenuItem.Visible = _revisionDiffController.ShouldShowMenuShowInFileTree(selectionInfo);
            fileHistoryDiffToolstripMenuItem.Enabled = _revisionDiffController.ShouldShowMenuFileHistory(selectionInfo);
            blameToolStripMenuItem.Enabled = _revisionDiffController.ShouldShowMenuBlame(selectionInfo);
            resetFileToToolStripMenuItem.Enabled = _revisionDiffController.ShouldShowResetFileMenus(selectionInfo);

            diffDeleteFileToolStripMenuItem.Visible = _revisionDiffController.ShouldShowMenuDeleteFile(selectionInfo);
            diffEditWorkingDirectoryFileToolStripMenuItem.Visible = _revisionDiffController.ShouldShowMenuEditWorkingDirectoryFile(selectionInfo);
            diffOpenWorkingDirectoryFileWithToolStripMenuItem.Visible = _revisionDiffController.ShouldShowMenuEditWorkingDirectoryFile(selectionInfo);
            diffOpenRevisionFileToolStripMenuItem.Visible = _revisionDiffController.ShouldShowMenuOpenRevision(selectionInfo);
            diffOpenRevisionFileWithToolStripMenuItem.Visible = _revisionDiffController.ShouldShowMenuOpenRevision(selectionInfo);

            diffCommitSubmoduleChanges.Visible =
                diffResetSubmoduleChanges.Visible =
                diffStashSubmoduleChangesToolStripMenuItem.Visible =
                diffSubmoduleSummaryMenuItem.Visible =
                diffUpdateSubmoduleMenuItem.Visible = _revisionDiffController.ShouldShowSubmoduleMenus(selectionInfo);

            diffToolStripSeparator13.Visible = _revisionDiffController.ShouldShowMenuDeleteFile(selectionInfo) ||
                                               _revisionDiffController.ShouldShowSubmoduleMenus(selectionInfo) ||
                                               _revisionDiffController.ShouldShowMenuEditWorkingDirectoryFile(selectionInfo) ||
                                               _revisionDiffController.ShouldShowMenuOpenRevision(selectionInfo);

            // openContainingFolderToolStripMenuItem.Enabled or not
            {
                openContainingFolderToolStripMenuItem.Enabled = false;

                foreach (var item in DiffFiles.SelectedItemsWithParent)
                {
                    string filePath = _fullPathResolver.Resolve(item.Item.Name);
                    if (FormBrowseUtil.FileOrParentDirectoryExists(filePath))
                    {
                        openContainingFolderToolStripMenuItem.Enabled = true;
                        break;
                    }
                }
            }
        }

        private void blameToolStripMenuItem_Click(object sender, EventArgs e)
        {
            GitItemStatusWithParent item = DiffFiles.SelectedItemWithParent;
            if (item?.SelectedRevision == null || item.Item == null || !item.Item.IsTracked)
            {
                return;
            }

            UICommands.StartFileHistoryDialog(this, item.Item.Name, item.SelectedRevision, true, true);
        }

        private void StageFileToolStripMenuItemClick(object sender, EventArgs e)
        {
            StageFiles();
        }

        private void StageFiles()
        {
            var files = DiffFiles.SelectedItemsWithParent.Where(item => item.Item.Staged == StagedStatus.WorkTree).Select(i => i.Item).ToList();

            Module.StageFiles(files, out _);
            RefreshArtificial();
        }

        private void UnstageFileToolStripMenuItemClick(object sender, EventArgs e)
        {
            UnstageFiles();
        }

        private void UnstageFiles()
        {
            Module.BatchUnstageFiles(DiffFiles.SelectedItemsWithParent.Where(item => item.Item.Staged == StagedStatus.Index).Select(i => i.Item).ToList());
            RefreshArtificial();
        }

        private void cherryPickSelectedDiffFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DiffText.CherryPickAllChanges();
        }

        private void copyFilenameToClipboardToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            FormBrowse.CopyFullPathToClipboard(DiffFiles, Module);
        }

        private void findInDiffToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var candidates = DiffFiles.GitItemStatuses;

            IEnumerable<GitItemStatus> FindDiffFilesMatches(string name)
            {
                var predicate = _findFilePredicateProvider.Get(name, Module.WorkingDir);
                return candidates.Where(item => predicate(item.Name) || predicate(item.OldName));
            }

            GitItemStatus selectedItem;
            using (var searchWindow = new SearchWindow<GitItemStatus>(FindDiffFilesMatches)
            {
                Owner = FindForm()
            })
            {
                searchWindow.ShowDialog(this);
                selectedItem = searchWindow.SelectedItem;
            }

            if (selectedItem != null)
            {
                DiffFiles.SelectedItem = selectedItem;
            }
        }

        private void fileHistoryDiffToolstripMenuItem_Click(object sender, EventArgs e)
        {
            GitItemStatusWithParent item = DiffFiles.SelectedItemWithParent;
            if (item?.SelectedRevision == null || item.Item == null || !item.Item.IsTracked)
            {
                return;
            }

            UICommands.StartFileHistoryDialog(this, item.Item.Name, item.SelectedRevision);
        }

        private void openContainingFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FormBrowse.OpenContainingFolder(DiffFiles, Module);
        }

        private void openWithDifftoolToolStripMenuItem_Click(object sender, EventArgs e)
        {
            RevisionDiffKind diffKind;

            if (sender == firstToLocalToolStripMenuItem)
            {
                diffKind = RevisionDiffKind.DiffALocal;
            }
            else if (sender == selectedToLocalToolStripMenuItem)
            {
                diffKind = RevisionDiffKind.DiffBLocal;
            }
            else if (sender == firstParentToLocalToolStripMenuItem)
            {
                diffKind = RevisionDiffKind.DiffAParentLocal;
            }
            else if (sender == selectedParentToLocalToolStripMenuItem)
            {
                diffKind = RevisionDiffKind.DiffBParentLocal;
            }
            else
            {
                diffKind = RevisionDiffKind.DiffAB;
            }

            foreach (var itemWithParent in DiffFiles.SelectedItemsWithParent)
            {
                if (itemWithParent.ParentRevision.ObjectId == ObjectId.CombinedDiffId)
                {
                    // CombinedDiff cannot be viewed in a difftool
                    // Disabled in menus but can be activated from shortcuts, just ignore
                    continue;
                }

                GitRevision[] revs = new[] { itemWithParent.SelectedRevision, itemWithParent.ParentRevision };
                UICommands.OpenWithDifftool(this, revs, itemWithParent.Item.Name, itemWithParent.Item.OldName, diffKind, itemWithParent.Item.IsTracked);
            }
        }

        private void diffEditWorkingDirectoryFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (DiffFiles.SelectedItemWithParent == null)
            {
                return;
            }

            var fileName = _fullPathResolver.Resolve(DiffFiles.SelectedItemWithParent.Item.Name);
            UICommands.StartFileEditorDialog(fileName);
            RefreshArtificial();
        }

        private void diffOpenWorkingDirectoryFileWithToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (DiffFiles.SelectedItemWithParent == null)
            {
                return;
            }

            var fileName = _fullPathResolver.Resolve(DiffFiles.SelectedItemWithParent.Item.Name);
            OsShellUtil.OpenAs(fileName.ToNativePath());
        }

        private void diffOpenRevisionFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SaveSelectedItemToTempFile(fileName => Process.Start(fileName));
        }

        private void diffOpenRevisionFileWithToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SaveSelectedItemToTempFile(OsShellUtil.OpenAs);
        }

        private void SaveSelectedItemToTempFile(Action<string> onSaved)
        {
            var item = DiffFiles.SelectedItemWithParent;
            if (item?.Item?.Name == null || item.SelectedRevision == null)
            {
                return;
            }

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;

                var blob = Module.GetFileBlobHash(item.Item.Name, item.SelectedRevision.ObjectId);

                if (blob == null)
                {
                    return;
                }

                var fileName = PathUtil.GetFileName(item.Item.Name);
                fileName = (Path.GetTempPath() + fileName).ToNativePath();
                Module.SaveBlobAs(fileName, blob.ToString());

                onSaved(fileName);
            }).FileAndForget();
        }

        private ContextMenuDiffToolInfo GetContextMenuDiffToolInfo()
        {
            // Some items are not supported if more than one revision is selected
            var revisions = DiffFiles.SelectedItemsWithParent.Select(item => item.SelectedRevision).Distinct().ToList();
            var selectedRev = revisions.Count() != 1 ? null : revisions.FirstOrDefault();

            var parentIds = DiffFiles.SelectedItemsWithParent.Select(i => i.ParentRevision.ObjectId).Distinct().ToList();
            bool firstIsParent = _gitRevisionTester.AllFirstAreParentsToSelected(parentIds, selectedRev);
            bool localExists = _gitRevisionTester.AnyLocalFileExists(DiffFiles.SelectedItemsWithParent.Select(i => i.Item));

            bool allAreNew = DiffFiles.SelectedItemsWithParent.All(i => i.Item.IsNew);
            bool allAreDeleted = DiffFiles.SelectedItemsWithParent.All(i => i.Item.IsDeleted);

            return new ContextMenuDiffToolInfo(
                selectedRevision: selectedRev,
                selectedItemParentRevs: parentIds,
                allAreNew: allAreNew,
                allAreDeleted: allAreDeleted,
                firstIsParent: firstIsParent,
                firstParentsValid: _revisionGrid.IsFirstParentValid(),
                localExists: localExists);
        }

        private void openWithDifftoolToolStripMenuItem_DropDownOpening(object sender, EventArgs e)
        {
            ContextMenuDiffToolInfo selectionInfo = GetContextMenuDiffToolInfo();
            var revisions = DiffFiles.SelectedItemsWithParent.Select(item => item.SelectedRevision).Distinct().ToList();

            if (revisions.Any())
            {
                selectedDiffCaptionMenuItem.Text = _selectedRevision + (DescribeRevision(revisions) ?? string.Empty);
                selectedDiffCaptionMenuItem.Visible = true;
                MenuUtil.SetAsCaptionMenuItem(selectedDiffCaptionMenuItem, DiffContextMenu);

                firstDiffCaptionMenuItem.Text = _firstRevision.Text +
                                                (DescribeRevision(DiffFiles.SelectedItemsWithParent.Select(i => i.ParentRevision).Distinct().ToList()) ?? string.Empty);
                firstDiffCaptionMenuItem.Visible = true;
                MenuUtil.SetAsCaptionMenuItem(firstDiffCaptionMenuItem, DiffContextMenu);
            }
            else
            {
                firstDiffCaptionMenuItem.Visible = false;
                selectedDiffCaptionMenuItem.Visible = false;
            }

            firstToSelectedToolStripMenuItem.Enabled = _revisionDiffContextMenuController.ShouldShowMenuFirstToSelected(selectionInfo);
            firstToLocalToolStripMenuItem.Enabled = _revisionDiffContextMenuController.ShouldShowMenuFirstToLocal(selectionInfo);
            selectedToLocalToolStripMenuItem.Enabled = _revisionDiffContextMenuController.ShouldShowMenuSelectedToLocal(selectionInfo);
            firstParentToLocalToolStripMenuItem.Enabled = _revisionDiffContextMenuController.ShouldShowMenuFirstParentToLocal(selectionInfo);
            selectedParentToLocalToolStripMenuItem.Enabled = _revisionDiffContextMenuController.ShouldShowMenuSelectedParentToLocal(selectionInfo);
            firstParentToLocalToolStripMenuItem.Visible = _revisionDiffContextMenuController.ShouldDisplayMenuFirstParentToLocal(selectionInfo);
            selectedParentToLocalToolStripMenuItem.Visible = _revisionDiffContextMenuController.ShouldDisplayMenuSelectedParentToLocal(selectionInfo);
        }

        private void resetFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ResetSelectedItemsTo(sender == resetFileToSelectedToolStripMenuItem);
        }

        /// <summary>
        /// Checks if it is possible to reset to the revision.
        /// For artificial is Index is possible but not WorkTree or Combined
        /// </summary>
        /// <param name="guid">The Git objectId</param>
        /// <returns>If it is possible to reset to the revisions</returns>
        private bool CanResetToRevision(ObjectId guid)
        {
            return guid != ObjectId.WorkTreeId
                   && guid != ObjectId.CombinedDiffId;
        }

        private void resetFileToToolStripMenuItem_DropDownOpening(object sender, EventArgs e)
        {
            var items = DiffFiles.SelectedItemsWithParent;
            var selectedIds = items.Select(it => it.SelectedRevision.ObjectId).Distinct().ToList();
            if (selectedIds.Count == 0)
            {
                resetFileToSelectedToolStripMenuItem.Visible = false;
                resetFileToParentToolStripMenuItem.Visible = false;
                return;
            }

            if (selectedIds.Count() != 1 || !CanResetToRevision(selectedIds.FirstOrDefault()))
            {
                resetFileToSelectedToolStripMenuItem.Visible = false;
            }
            else
            {
                resetFileToSelectedToolStripMenuItem.Visible = true;
                resetFileToSelectedToolStripMenuItem.Text =
                    _selectedRevision + DescribeRevision(selectedIds.FirstOrDefault(), 50);
            }

            var parentIds = DiffFiles.SelectedItemsWithParent.Select(i => i.ParentRevision.ObjectId).Distinct().ToList();
            if (parentIds.Count != 1 || !CanResetToRevision(parentIds.FirstOrDefault()))
            {
                resetFileToParentToolStripMenuItem.Visible = false;
            }
            else
            {
                resetFileToParentToolStripMenuItem.Visible = true;
                resetFileToParentToolStripMenuItem.Text =
                    _firstRevision + DescribeRevision(parentIds.FirstOrDefault(), 50);
            }
        }

        private void saveAsToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            GitItemStatusWithParent item = DiffFiles.SelectedItemWithParent;
            if (item?.SelectedRevision == null || item.Item == null)
            {
                return;
            }

            var fullName = _fullPathResolver.Resolve(item.Item.Name);
            using (var fileDialog =
                new SaveFileDialog
                {
                    InitialDirectory = Path.GetDirectoryName(fullName),
                    FileName = Path.GetFileName(fullName),
                    DefaultExt = Path.GetExtension(fullName),
                    AddExtension = true
                })
            {
                fileDialog.Filter =
                    _saveFileFilterCurrentFormat.Text + " (*." +
                    fileDialog.DefaultExt + ")|*." +
                    fileDialog.DefaultExt +
                    "|" + _saveFileFilterAllFiles.Text + " (*.*)|*.*";

                if (fileDialog.ShowDialog(this) == DialogResult.OK)
                {
                    Module.SaveBlobAs(fileDialog.FileName, $"{item.SelectedRevision?.Guid}:\"{item.Item.Name}\"");
                }
            }
        }

        private bool DeleteSelectedFiles()
        {
            try
            {
                var selected = DiffFiles.SelectedItemWithParent;
                if (selected?.Item == null || selected.SelectedRevision == null || !selected.SelectedRevision.IsArtificial ||
                    MessageBox.Show(this, _deleteSelectedFiles.Text, _deleteSelectedFilesCaption.Text, MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning) !=
                    DialogResult.Yes)
                {
                    return false;
                }

                // If any file is staged, it must be unstaged
                Module.BatchUnstageFiles(DiffFiles.SelectedItemsWithParent.Where(item => item.Item.Staged == StagedStatus.Index).Select(item => item.Item));

                DiffFiles.StoreNextIndexToSelect();
                var items = DiffFiles.SelectedItemsWithParent.Where(item => !item.Item.IsSubmodule);
                foreach (var item in items)
                {
                    var path = _fullPathResolver.Resolve(item.Item.Name);
                    bool isDir = (File.GetAttributes(path) & FileAttributes.Directory) == FileAttributes.Directory;
                    if (isDir)
                    {
                        Directory.Delete(path, true);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }

                RefreshArtificial();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, _deleteFailed.Text + Environment.NewLine + ex.Message, Strings.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            return true;
        }

        private void diffDeleteFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DeleteSelectedFiles();
        }

        private void diffCommitSubmoduleChanges_Click(object sender, EventArgs e)
        {
            var submodules = DiffFiles.SelectedItemsWithParent.Where(it => it.Item.IsSubmodule).Select(it => it.Item.Name).Distinct().ToList();

            foreach (var name in submodules)
            {
                var submodulCommands = new GitUICommands(_fullPathResolver.Resolve(name.EnsureTrailingPathSeparator()));
                submodulCommands.StartCommitDialog(this);
            }

            RefreshArtificial();
        }

        private void diffResetSubmoduleChanges_Click(object sender, EventArgs e)
        {
            var submodules = DiffFiles.SelectedItemsWithParent.Where(it => it.Item.IsSubmodule).Select(it => it.Item.Name).Distinct().ToList();

            // Show a form asking the user if they want to reset the changes.
            FormResetChanges.ActionEnum resetType = FormResetChanges.ShowResetDialog(this, true, true);
            if (resetType == FormResetChanges.ActionEnum.Cancel)
            {
                return;
            }

            foreach (var name in submodules)
            {
                GitModule module = Module.GetSubmodule(name);

                // Reset all changes.
                module.Reset(ResetMode.Hard);

                // Also delete new files, if requested.
                if (resetType == FormResetChanges.ActionEnum.ResetAndDelete)
                {
                    module.Clean(CleanMode.OnlyNonIgnored, directories: true);
                }
            }

            RefreshArtificial();
        }

        private void diffUpdateSubmoduleMenuItem_Click(object sender, EventArgs e)
        {
            var submodules = DiffFiles.SelectedItemsWithParent.Where(it => it.Item.IsSubmodule).Select(it => it.Item.Name).Distinct().ToList();

            FormProcess.ShowDialog(FindForm() as FormBrowse, GitCommandHelpers.SubmoduleUpdateCmd(submodules));
            RefreshArtificial();
        }

        private void diffStashSubmoduleChangesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var submodules = DiffFiles.SelectedItemsWithParent.Where(it => it.Item.IsSubmodule).Select(it => it.Item.Name).Distinct().ToList();

            foreach (var name in submodules)
            {
                var uiCmds = new GitUICommands(Module.GetSubmodule(name));
                uiCmds.StashSave(this, AppSettings.IncludeUntrackedFilesInManualStash);
            }

            RefreshArtificial();
        }

        private void diffSubmoduleSummaryMenuItem_Click(object sender, EventArgs e)
        {
            var submodules = DiffFiles.SelectedItemsWithParent.Where(it => it.Item.IsSubmodule).Select(it => it.Item.Name).Distinct().ToList();

            string summary = "";
            foreach (var name in submodules)
            {
                summary += Module.GetSubmoduleSummary(name);
            }

            using (var frm = new FormEdit(UICommands, summary))
            {
                frm.ShowDialog(this);
            }
        }

        public void SwitchFocus(bool alreadyContainedFocus)
        {
            if (alreadyContainedFocus && DiffFiles.Focused)
            {
                DiffText.Focus();
            }
            else
            {
                DiffFiles.Focus();
            }
        }

        /// <summary>
        /// Hotkey handler
        /// </summary>
        /// <returns>true if executed</returns>
        private bool StageSelectedFile()
        {
            if (DiffFiles.SelectedItemWithParent.SelectedRevision.ObjectId == ObjectId.IndexId)
            {
                return false;
            }

            if (DiffText.ContainsFocus && DiffText.SupportLinePatching)
            {
                StageOrCherryPickSelectedLines();
                return true;
            }
            else if (DiffFiles.Focused && DiffFiles.SelectedItemWithParent.SelectedRevision.ObjectId == ObjectId.WorkTreeId)
            {
                // Only stage files for WorkTree (even if lines can be cherry-picked/staged)
                StageFiles();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Hotkey handler
        /// </summary>
        /// <returns>true if executed</returns>
        private bool UnStageSelectedFile()
        {
            if (DiffFiles.SelectedItemWithParent.SelectedRevision.ObjectId != ObjectId.IndexId)
            {
                return false;
            }

            if (DiffText.ContainsFocus && DiffText.SupportLinePatching)
            {
                StageOrCherryPickSelectedLines();
                return true;
            }
            else if (DiffFiles.Focused)
            {
                UnstageFiles();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Hotkey handler
        /// </summary>
        /// <returns>true if executed</returns>
        private bool ResetSelectedFiles()
        {
            var parentIds = DiffFiles.SelectedItemsWithParent.Select(it => it.SelectedRevision.ObjectId).ToList();
            if (DiffText.ContainsFocus && DiffText.SupportLinePatching)
            {
                ResetOrRevertSelectedLines();
                return true;
            }
            else if ((DiffFiles.Focused && parentIds.Count != 1) || !CanResetToRevision(parentIds.FirstOrDefault()))
            {
                // Reset to first (parent)
                ResetSelectedItemsTo(actsAsChild: false);
                return true;
            }

            return false;
        }

        private void DiffText_TextLoaded(object sender, EventArgs e)
        {
            _selectedDiffReloaded = true;

            var item = DiffFiles?.SelectedItemWithParent;
            if (item?.Item == null)
            {
                return;
            }

            // Show the menu texts etc for the currently displayed file
            if (IsWorkTreeOrIndexWithParent())
            {
                // HEAD -> Index, Index -> Worktree allows unstaging/staging
                if (item.SelectedRevision.ObjectId == ObjectId.IndexId)
                {
                    DiffText.CherryPickContextMenuEntry_Update(
                        Strings.UnstageSelectedLines,
                        Properties.Images.Unstage,
                        GetShortcutKeyDisplayString(Command.UnStageSelectedFile));
                }
                else
                {
                    DiffText.CherryPickContextMenuEntry_Update(
                        Strings.StageSelectedLines,
                        Properties.Images.Stage,
                        GetShortcutKeyDisplayString(Command.StageSelectedFile));
                }

                DiffText.RevertSelectedContextMenuEntry_Update(
                    Strings.ResetSelectedLines,
                    Properties.Images.ResetWorkingDirChanges,
                    GetShortcutKeyDisplayString(Command.ResetSelectedFiles));

                if (item.Item.IsNew && !FileHelper.IsImage(item.Item.Name))
                {
                    // Add visibility, not for normal file displays
                    DiffText.CherryPickContextMenuEntry_Visible();
                    DiffText.RevertSelectedContextMenuEntry_Visible();
                }

                return;
            }

            DiffText.CherryPickContextMenuEntry_Update(
                Strings.CherrypickSelectedLines,
                Properties.Images.CherryPick,
                GetShortcutKeyDisplayString(Command.StageSelectedFile));

            DiffText.RevertSelectedContextMenuEntry_Update(
                Strings.RevertSelectedLines,
                Properties.Images.ResetFileTo,
                GetShortcutKeyDisplayString(Command.ResetSelectedFiles));
        }

        /// <summary>
        /// Check if the commits for the currenly selected item is the same as in FormCommit,
        /// i.e. HEAD->Index or Index->WorkTree
        /// </summary>
        /// <returns>If 'artificial' handling</returns>
        private bool IsWorkTreeOrIndexWithParent()
        {
            var item = DiffFiles.SelectedItemWithParent;
            if (item?.Item == null ||
                item.SelectedRevision?.ObjectId == null ||
                !item.SelectedRevision.IsArtificial)
            {
                return false;
            }

            return (item.SelectedRevision.ObjectId == ObjectId.WorkTreeId
                    || item.SelectedRevision.ObjectId == ObjectId.IndexId)
                   && item.SelectedRevision.FirstParentGuid == item.ParentRevision.ObjectId;
        }

        private void StageSelectedLinesToolStripMenuItemClick(object sender, EventArgs e)
        {
            StageOrCherryPickSelectedLines();
        }

        private void StageOrCherryPickSelectedLines()
        {
            // to prevent multiple clicks
            if (!_selectedDiffReloaded)
            {
                return;
            }

            // File no longer selected
            if (DiffFiles.SelectedItemWithParent == null)
            {
                return;
            }

            if (IsWorkTreeOrIndexWithParent())
            {
                StageSelectedLines();
                return;
            }

            applySelectedLines(false);
        }

        /// <summary>
        /// apply patches
        /// Similar to FileViewer.applySelectedLines()
        /// </summary>
        /// <param name="reverse">set if patches is to be reversed</param>
        private void applySelectedLines(bool reverse)
        {
            byte[] patch;
            if (reverse)
            {
                patch = PatchManager.GetResetWorkTreeLinesAsPatch(
                    Module, DiffText.GetText(),
                    DiffText.GetSelectionPosition(), DiffText.GetSelectionLength(), DiffText.Encoding);
            }
            else
            {
                patch = PatchManager.GetSelectedLinesAsPatch(
                    DiffText.GetText(),
                    DiffText.GetSelectionPosition(), DiffText.GetSelectionLength(),
                    false, DiffText.Encoding, false);
            }

            if (patch == null || patch.Length == 0)
            {
                return;
            }

            var args = new GitArgumentBuilder("apply")
            {
                "--3way",
                "--index",
                "--whitespace=nowarn"
            };

            string output = Module.GitExecutable.GetOutput(args, patch);
            ProcessApplyOutput(output, patch);
        }

        /// <summary>
        /// Stage lines in WorkTree or Unstage lines in Index
        /// </summary>
        private void StageSelectedLines()
        {
            byte[] patch;
            var item = DiffFiles.SelectedItemWithParent;
            var currentItemStaged = item.SelectedRevision.ObjectId == ObjectId.IndexId;
            if (item.Item.IsNew)
            {
                var treeGuid = currentItemStaged ? item.Item.TreeGuid?.ToString() : null;
                patch = PatchManager.GetSelectedLinesAsNewPatch(Module, item.Item.Name,
                    DiffText.GetText(), DiffText.GetSelectionPosition(),
                    DiffText.GetSelectionLength(), DiffText.Encoding, false, DiffText.FilePreamble, treeGuid);
            }
            else
            {
                patch = PatchManager.GetSelectedLinesAsPatch(
                    DiffText.GetText(),
                    DiffText.GetSelectionPosition(), DiffText.GetSelectionLength(),
                    currentItemStaged, DiffText.Encoding, item.Item.IsNew);
            }

            if (patch == null || patch.Length == 0)
            {
                return;
            }

            var args = new GitArgumentBuilder("apply")
            {
                "--cached",
                "--index",
                "--whitespace=nowarn",
                { currentItemStaged, "--reverse" }
            };

            string output = Module.GitExecutable.GetOutput(args, patch);
            ProcessApplyOutput(output, patch);
        }

        private void ResetSelectedLinesToolStripMenuItemClick(object sender, EventArgs e)
        {
            ResetOrRevertSelectedLines();
        }

        private void ResetOrRevertSelectedLines()
        {
            // to prevent multiple clicks
            if (!_selectedDiffReloaded)
            {
                return;
            }

            // File no longer selected
            if (DiffFiles.SelectedItemWithParent == null)
            {
                return;
            }

            if (MessageBox.Show(this, Strings.ResetSelectedLinesConfirmation, Strings.ResetChangesCaption,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
            {
                return;
            }

            if (IsWorkTreeOrIndexWithParent())
            {
                ResetSelectedLines();
                return;
            }

            applySelectedLines(true);
        }

        /// <summary>
        /// Reset lines in Index or Worktree
        /// </summary>
        private void ResetSelectedLines()
        {
            byte[] patch;
            var item = DiffFiles.SelectedItemWithParent;
            var currentItemStaged = item.SelectedRevision.ObjectId == ObjectId.IndexId;
            if (item.Item.IsNew)
            {
                var treeGuid = currentItemStaged ? item.Item.TreeGuid?.ToString() : null;
                patch = PatchManager.GetSelectedLinesAsNewPatch(Module, item.Item.Name,
                    DiffText.GetText(), DiffText.GetSelectionPosition(), DiffText.GetSelectionLength(),
                    DiffText.Encoding, true, DiffText.FilePreamble, treeGuid);
            }
            else if (currentItemStaged)
            {
                patch = PatchManager.GetSelectedLinesAsPatch(
                    DiffText.GetText(),
                    DiffText.GetSelectionPosition(), DiffText.GetSelectionLength(),
                    currentItemStaged, DiffText.Encoding, item.Item.IsNew);
            }
            else
            {
                patch = PatchManager.GetResetWorkTreeLinesAsPatch(Module, DiffText.GetText(),
                    DiffText.GetSelectionPosition(), DiffText.GetSelectionLength(), DiffText.Encoding);
            }

            if (patch == null || patch.Length == 0)
            {
                return;
            }

            var args = new GitArgumentBuilder("apply")
            {
                "--whitespace=nowarn",
                { currentItemStaged, "--reverse --index" }
            };

            string output = Module.GitExecutable.GetOutput(args, patch);

            if (EnvUtils.RunningOnWindows())
            {
                // remove file mode warnings on windows
                var regEx = new Regex("warning: .*has type .* expected .*", RegexOptions.Compiled);
                output = output.RemoveLines(regEx.IsMatch);
            }

            ProcessApplyOutput(output, patch);
        }

        private void ProcessApplyOutput(string output, byte[] patch)
        {
            if (!string.IsNullOrEmpty(output))
            {
                MessageBox.Show(this, output + "\n\n" + DiffText.Encoding.GetString(patch), Strings.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            _selectedDiffReloaded = false;
            RefreshArtificial();
        }
    }
}
