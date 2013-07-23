﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using dlech.SshAgentLib;
using KeePass.App;
using KeePass.Plugins;
using KeePass.UI;
using KeePassLib;
using KeePassLib.Utility;

namespace KeeAgent.UI
{
  public partial class EntryPickerDialog : Form
  {
    public IPluginHost mPluginHost;
    public DateTime mCachedNow;
    public PwDatabase mActiveDb;
    public Font mExpiredFont, mBoldFont, mItalicFont;

    public PwEntry SelectedEntry { get; private set; }

    public ICollection<Agent.KeyConstraint> Constraints
    {
      get
      {
        var constraints = new List<Agent.KeyConstraint>();
        if (mConfirmConstraintControl.Checked) {
          constraints.addConfirmConstraint();
        }
        if (mLifetimeConstraintControl.Checked) {
          constraints.addLifetimeConstraint(mLifetimeConstraintControl.Lifetime);
        }
        return constraints;
      }
    }

    public EntryPickerDialog(IPluginHost aPluginHost, bool aShowConstraintControls)
    {
      mPluginHost = aPluginHost;
      InitializeComponent();
      if (!aShowConstraintControls) {
        Controls.Remove(mTableLayoutPanel);
        mCustomTreeViewEx.Height += mTableLayoutPanel.Height + 6;
      }

#if __MonoCS__
      Icon = Properties.Resources.KeeAgent_icon_mono;
      // on windows, help button is displayed in the title bar
      // on mono, we need to add one in the window
        var helpButton = new Button();
        helpButton.Size = new Size(25, 25);
      helpButton.Image = Properties.Resources.Help_png;
      helpButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        helpButton.Location = new Point(mCustomTreeViewEx.Location.X,
                                        mOkButton.Location.Y);
        helpButton.Click += (sender, e) => OnHelpRequested();
        Controls.Add(helpButton);
#else
      Icon = Properties.Resources.KeeAgent_icon;
#endif

      mExpiredFont = FontUtil.CreateFont(mCustomTreeViewEx.Font, FontStyle.Strikeout);
      mBoldFont = FontUtil.CreateFont(mCustomTreeViewEx.Font, FontStyle.Bold);
      mItalicFont = FontUtil.CreateFont(mCustomTreeViewEx.Font, FontStyle.Italic);
      InitalizeList(false);
    }

    void InitalizeList(bool autodetect)
    {
      mCustomTreeViewEx.BeginUpdate();
      mCustomTreeViewEx.Nodes.Clear();
      mCachedNow = DateTime.Now;
      bool entriesFound = false;

      foreach (var db in mPluginHost.MainWindow.DocumentManager.GetOpenDatabases()) {
        mActiveDb = db;
        UpdateImageLists();

        TreeNode rootNode = null;
        var rootGroup = mActiveDb.RootGroup;
        if (rootGroup != null) {
          int nIconID = ((!rootGroup.CustomIconUuid.EqualsValue(PwUuid.Zero)) ?
            ((int)PwIcon.Count + mActiveDb.GetCustomIconIndex(
            rootGroup.CustomIconUuid)) : (int)rootGroup.IconId);
          if (rootGroup.Expires && (rootGroup.ExpiryTime <= mCachedNow)) {
            nIconID = (int)PwIcon.Expired;
          }

          rootNode = new TreeNode(rootGroup.Name, nIconID, nIconID);
          rootNode.Tag = rootGroup;
          rootNode.ForeColor = SystemColors.GrayText;

          if (mBoldFont != null) {
            rootNode.NodeFont = mBoldFont;
          }
          rootNode.ToolTipText = db.IOConnectionInfo.GetDisplayName();

          mCustomTreeViewEx.Nodes.Add(rootNode);
        }

        entriesFound |= RecursiveAddGroup(rootNode, rootGroup, autodetect);

        if (rootNode != null) {
          rootNode.Expand();
        }
      }
      mCustomTreeViewEx.EndUpdate();

      if (!entriesFound) {
        if (autodetect) {
          MessageService.ShowWarning("No entries with SSH keys were found.");
          Close();
        } else {
          // Use timer so that this dialog finishes displaying before attempting
          // the auto-detect routine.
          var autodetectDialogDelayTimer = new Timer();
          autodetectDialogDelayTimer.Interval = 100;
          autodetectDialogDelayTimer.Tick += (sender, e) =>
          {
            autodetectDialogDelayTimer.Stop();
            AskShouldAutodetect();
          };
          autodetectDialogDelayTimer.Start();
        }
      }
    }

    void AskShouldAutodetect()
    {
      var result = MessageService.AskYesNo(
        "No KeePass database entries are enabled for use with KeeAgent." +
        " Would you like to attempt to auto-detect and enable entries with SSH keys?",
        "KeeAgent", true);
      if (result) {
        InitalizeList(true);
      } else {
        Close();
      }
    }

    bool RecursiveAddGroup(TreeNode parentNode, PwGroup parentGroup, bool autodetect)
    {
      if (parentGroup == null)
        return false;

      TreeNodeCollection treeNodes;
      if (parentNode == null)
        treeNodes = mCustomTreeViewEx.Nodes;
      else
        treeNodes = parentNode.Nodes;

      bool entriesFound = false;

      foreach (PwGroup childGroup in parentGroup.Groups) {
        if (mActiveDb.RecycleBinEnabled &&
            childGroup.Uuid.EqualsValue(mActiveDb.RecycleBinUuid))
          continue;

        bool bExpired = (childGroup.Expires && (childGroup.ExpiryTime <= mCachedNow));
        string strName = childGroup.Name;

        int iconID = ((!childGroup.CustomIconUuid.EqualsValue(PwUuid.Zero)) ?
          ((int)PwIcon.Count + mActiveDb.GetCustomIconIndex(childGroup.CustomIconUuid)) :
          (int)childGroup.IconId);
        if (bExpired) {
          iconID = (int)PwIcon.Expired;
        }

        var newNode = new TreeNode(strName, iconID, iconID);
        newNode.Tag = childGroup;
        newNode.ForeColor = SystemColors.GrayText;
        UIUtil.SetGroupNodeToolTip(newNode, childGroup);

        if (bExpired && (mExpiredFont != null))
          newNode.NodeFont = mExpiredFont;

        treeNodes.Add(newNode);

        entriesFound |= RecursiveAddGroup(newNode, childGroup, autodetect);

        foreach (var entry in childGroup.Entries) {
          var settings = entry.GetKeeAgentSettings();
          if (autodetect) {
            var entryClone = entry.CloneDeep();
            settings.AllowUseOfSshKey = true;
            settings.Location.SelectedType = EntrySettings.LocationType.Attachment;
            var sshKeyFound = false;
            foreach (var attachment in entry.Binaries) {
              try {
                settings.Location.AttachmentName = attachment.Key;
                entryClone.SetKeeAgentSettings(settings);
                entryClone.GetSshKey(); // throws
                entry.SetKeeAgentSettings(settings);
                entry.Touch(true);
                mActiveDb.Modified = true;
                mPluginHost.MainWindow.UpdateUI(false, null, false, null, false, null, false);
                sshKeyFound = true;
                break;
              } catch (Exception) {
                // ignore all errors
              }
            }
            if (!sshKeyFound)
              continue;
          }
          if (settings.AllowUseOfSshKey) {
            var entryNode = new TreeNode(entry.Strings.Get(PwDefs.TitleField).ReadString(),
              (int)entry.IconId, (int)entry.IconId);
            entryNode.Tag = entry;

            if (entry.Expires && (entry.ExpiryTime <= mCachedNow)) {
              entryNode.ImageIndex = (int)PwIcon.Expired;
              if (mExpiredFont != null) entryNode.NodeFont = mExpiredFont;
            } else { // Not expired			
              if (entry.CustomIconUuid.EqualsValue(PwUuid.Zero))
                entryNode.ImageIndex = (int)entry.IconId;
              else
                entryNode.ImageIndex = (int)PwIcon.Count +
                  mActiveDb.GetCustomIconIndex(entry.CustomIconUuid);
            }
            entryNode.ForeColor = entry.ForegroundColor;
            entryNode.BackColor = entryNode.BackColor;
            newNode.Nodes.Add(entryNode);
            entriesFound = true;
          }
        }

        if (newNode.Nodes.Count > 0) {
          if ((newNode.IsExpanded) && (!childGroup.IsExpanded)) {
            newNode.Collapse();
          } else if ((!newNode.IsExpanded) && (childGroup.IsExpanded)) {
            newNode.Expand();
          }
        }
      }
      return entriesFound;
    }

    private void UpdateImageLists()
    {
      ImageList imgList = new ImageList();
      imgList.ImageSize = new Size(16, 16);
      imgList.ColorDepth = ColorDepth.Depth32Bit;

      List<Image> lStdImages = new List<Image>();
      foreach (Image imgStd in mPluginHost.MainWindow.ClientIcons.Images) {
        lStdImages.Add(imgStd);
      }
      imgList.Images.AddRange(lStdImages.ToArray());

      Debug.Assert(imgList.Images.Count == (int)PwIcon.Count);

      List<Image> lCustom = UIUtil.BuildImageListEx(
       mActiveDb.CustomIcons, 16, 16);
      if ((lCustom != null) && (lCustom.Count > 0))
        imgList.Images.AddRange(lCustom.ToArray());

      if (UIUtil.VistaStyleListsSupported) {
        mCustomTreeViewEx.ImageList = imgList;
      } else {
        List<Image> vAllImages = new List<Image>();
        foreach (Image imgClient in imgList.Images)
          vAllImages.Add(imgClient);
        vAllImages.AddRange(lCustom);
        Debug.Assert(imgList.Images.Count == vAllImages.Count);

        ImageList imgSafe = UIUtil.ConvertImageList24(vAllImages, 16, 16,
          AppDefs.ColorControlNormal);
        mCustomTreeViewEx.ImageList = imgSafe;
      }
    }

    private void customTreeViewEx_NodeMouseDoubleClick(object sender,
      TreeNodeMouseClickEventArgs e)
    {
      var entry = e.Node.Tag as PwEntry;
      if (entry != null)      {
        AcceptButton.PerformClick();
      }
    }

    private void customTreeViewEx_AfterSelect(object sender, TreeViewEventArgs e)
    {
      SelectedEntry = e.Node.Tag as PwEntry;
    }

    private void EntryPickerDialog_FormClosing(object sender,
      FormClosingEventArgs e)
    {
      if (DialogResult == DialogResult.OK) {
        if (SelectedEntry == null) {
          MessageBox.Show("Must select an entry", Util.AssemblyTitle,
             MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
          e.Cancel = true;
        }
        if (mLifetimeConstraintControl.Checked &&
          mLifetimeConstraintControl.Lifetime == 0)
        {
          MessageBox.Show("Invalid lifetime", Util.AssemblyTitle,
             MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
          e.Cancel = true;
        }
      }
    }

    private void EntryPickerDialog_HelpButtonClicked(object sender, CancelEventArgs e)
    {
      OnHelpRequested();
      e.Cancel = true;
    }

    private void EntryPickerDialog_HelpRequested(object sender, HelpEventArgs hlpevent)
    {
      OnHelpRequested();
    }

    private void OnHelpRequested()
    {
      Process.Start(Properties.Resources.WebHelpEntryPicker);
    }
  }
}
