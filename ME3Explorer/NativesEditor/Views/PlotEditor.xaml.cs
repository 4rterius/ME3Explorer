﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Gammtek.Conduit.MassEffect3.SFXGame.CodexMap;
using Gammtek.Conduit.MassEffect3.SFXGame.QuestMap;
using Gammtek.Conduit.MassEffect3.SFXGame.StateEventMap;
using ME3Explorer;
using ME3Explorer.ME3ExpMemoryAnalyzer;
using ME3Explorer.SharedUI.Interfaces;
using ME3ExplorerCore.Helpers;
using ME3ExplorerCore.Misc;
using ME3ExplorerCore.Packages;
using Microsoft.AppCenter.Analytics;
using Microsoft.Win32;

namespace MassEffect.NativesEditor.Views
{
    public partial class PlotEditor : WPFBase, IRecents
    {
        public PlotEditor()
        {
            MemoryAnalyzer.AddTrackedMemoryItem(new MemoryAnalyzerObjectExtended("Plot Editor", new WeakReference(this)));
            Analytics.TrackEvent("Used tool", new Dictionary<string, string>()
            {
                { "Toolname", "Plot Editor" }
            });
            InitializeComponent();
            RecentsController.InitRecentControl(Toolname, Recents_MenuItem, fileName => LoadFile(fileName));
            
            FindObjectUsagesControl.parentRef = this;
        }

        public string CurrentFile => Pcc != null ? Path.GetFileName(Pcc.FilePath) : "Select a file to load";

        public void OpenFile()
        {
            var dlg = new OpenFileDialog { Filter = "Support files|*.pcc;*.upk", Multiselect = false };

            if (dlg.ShowDialog() != true)
            {
                return;
            }

            LoadFile(dlg.FileName);

        }

        public void LoadFile(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (!File.Exists(path))
            {
                return;
            }

            LoadMEPackage(path);

            CodexMapControl?.Open(Pcc);

            QuestMapControl?.Open(Pcc);

            StateEventMapControl?.Open(Pcc);

            RecentsController.AddRecent(path, false);
            RecentsController.SaveRecentList(true);
            Title = $"Plot Editor - {path}";
            OnPropertyChanged(nameof(CurrentFile));

            //Hiding "Recents" panel
            if (MainTabControl.SelectedIndex == 0)
            {
                MainTabControl.SelectedIndex = 1; 
            }
        }

        public void SaveFile(string filepath = null)
        {
            if (Pcc == null)
            {
                return;
            }

            if (CodexMapControl != null)
            {

                if (CodexMapView.TryFindCodexMap(Pcc, out ExportEntry export, out int _))
                {
                    using (var stream = new MemoryStream())
                    {
                        var codexMap = CodexMapControl.ToCodexMap();
                        var binaryCodexMap = new BinaryBioCodexMap(codexMap.Sections, codexMap.Pages);

                        binaryCodexMap.Save(stream);

                        export.SetBinaryData(stream.ToArray());
                    }
                }
            }

            if (QuestMapControl != null)
            {

                if (QuestMapControl.TryFindQuestMap(Pcc, out ExportEntry export, out int _))
                {
                    using (var stream = new MemoryStream())
                    {
                        var questMap = QuestMapControl.ToQuestMap();
                        var binaryQuestMap = new BinaryBioQuestMap(questMap.Quests, questMap.BoolTaskEvals, questMap.IntTaskEvals, questMap.FloatTaskEvals);

                        binaryQuestMap.Save(stream);

                        export.SetBinaryData(stream.ToArray());
                    }
                }
            }

            if (StateEventMapControl != null)
            {

                if (StateEventMapView.TryFindStateEventMap(Pcc, out ExportEntry export))
                {
                    using (var stream = new MemoryStream())
                    {
                        var stateEventMap = StateEventMapControl.ToStateEventMap();
                        var binaryStateEventMap = new BinaryBioStateEventMap(stateEventMap.StateEvents);

                        binaryStateEventMap.Save(stream);

                        export.SetBinaryData(stream.ToArray());
                    }
                }
            }

            if (filepath == null)
                filepath = Pcc.FilePath;

            Pcc.Save(filepath);
        }

        public void SaveFileAs()
        {
            var dlg = new SaveFileDialog { Filter = "Support files|*.pcc;*.upk" };

            if (dlg.ShowDialog() != true)
            {
                return;
            }

            SaveFile(dlg.FileName);
        }

        public override void handleUpdate(List<PackageUpdate> updates)
        {
            //TODO: implement handleUpdate
        }

        private void Save_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = Pcc != null;
        }

        private void Save_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SaveFile();
        }

        private void SaveAs_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SaveFileAs();
        }

        private void Open_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true;
        }

        private void Open_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            OpenFile();
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string ext = Path.GetExtension(files[0]).ToLower();
                if (ext != ".upk" && ext != ".pcc")
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string ext = Path.GetExtension(files[0]).ToLower();
                if (ext == ".upk" || ext == ".pcc")
                {
                    LoadFile(files[0]);
                }
            }
        }

        public void GoToStateEvent(int id)
        {
            MainTabControl.SelectedValue = StateEventMapControl;
            StateEventMapControl.SelectedStateEvent = StateEventMapControl.StateEvents.FirstOrDefault(kvp => kvp.Key == id);
            StateEventMapControl.StateEventMapListBox.ScrollIntoView(StateEventMapControl.SelectedStateEvent);
            StateEventMapControl.StateEventMapListBox.Focus();
        }

        public void PropogateRecentsChange(IEnumerable<string> newRecents)
        {
            RecentsController.PropogateRecentsChange(false, newRecents);
        }

        public string Toolname => "NativesEditor";
    }
}
