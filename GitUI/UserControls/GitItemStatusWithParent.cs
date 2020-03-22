using GitCommands;

namespace GitUI.UserControls
{
    public sealed class GitItemStatusWithParent
    {
        public GitItemStatusWithParent(GitRevision parent, GitRevision selected, GitItemStatus item)
        {
            SelectedRevision = selected;
            ParentRevision = parent;
            Item = item;
        }

        /// <summary>
        /// Selected (current or B in diff)
        /// </summary>
        public GitRevision SelectedRevision { get; }

        /// <summary>
        /// First (Parent or A in diff)
        /// </summary>
        public GitRevision ParentRevision { get; }
        public GitItemStatus Item { get; }
        public override string ToString() => Item.ToString();
    }
}
