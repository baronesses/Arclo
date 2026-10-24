using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Orbit.Core;
using Orbit.Core.Documents;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private readonly List<PasswordBox> _sevenZipPasswordBoxes=new();
    private SevenZipPassword? _sevenZipBrowsePassword;

    private void ClearSevenZipPasswords()
    {
        foreach(var box in _sevenZipPasswordBoxes)box.Clear();
        _sevenZipPasswordBoxes.Clear();
        _sevenZipBrowsePassword?.Dispose();_sevenZipBrowsePassword=null;
    }
    private PasswordBox AddSevenZipPasswordField(Panel panel,string label,string tag)
    {
        panel.Children.Add(Text(label,12));
        var box=new PasswordBox{Tag=tag,MaxLength=256,Margin=new Thickness(0,2,0,7)};
        System.Windows.Automation.AutomationProperties.SetName(box,label);
        _sevenZipPasswordBoxes.Add(box);panel.Children.Add(box);return box;
    }
    private static char[] SecureCharacters(SecureString secure)
    {
        var pointer=IntPtr.Zero;var characters=new char[secure.Length];
        try{
            pointer=Marshal.SecureStringToGlobalAllocUnicode(secure);
            for(int i=0;i<characters.Length;i++)characters[i]=(char)Marshal.ReadInt16(pointer,i*2);
            return characters;
        }catch{CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));throw;
        }finally{if(pointer!=IntPtr.Zero)Marshal.ZeroFreeGlobalAllocUnicode(pointer);}
    }
    private static SevenZipPassword ReadSevenZipPassword(PasswordBox box,PasswordBox? confirmation=null)
    {
        using var secure=box.SecurePassword;var characters=SecureCharacters(secure);char[]? other=null;
        try{
            if(confirmation is not null){
                using var confirmed=confirmation.SecurePassword;other=SecureCharacters(confirmed);
                if(!CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(characters.AsSpan()),MemoryMarshal.AsBytes(other.AsSpan())))
                    throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_03B304199CC5"));
            }
            return new SevenZipPassword(characters);
        }finally{
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
            if(other is not null)CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(other.AsSpan()));
        }
    }
    private void QueueSevenZipWithPassword(IReadOnlyList<string> paths,ImageJobOptions options,SevenZipPassword password)
    {
        using(password){
            if(_closing||paths.Count==0)return;
            if(options.Action==ImageAction.Archive7z)Enqueue(paths[0],options,paths,password.Clone());
            else foreach(var path in paths)Enqueue(path,options,new[]{path},password.Clone());
        }
        if(_compact)ClosePanel();else ShowJobs();
    }
    private void ShowSevenZipPasswordRetry(IReadOnlyList<string> paths,ImageJobOptions options)
    {
        // History contains only encryption flags; each retry receives a new operation credential.
        ShowSevenZipPasswordRequest(paths,options,false);
    }
    private void ShowSevenZipPasswordRequest(IReadOnlyList<string> sourcePaths,ImageJobOptions options,bool bindSelection)
    {
        var paths=sourcePaths.ToArray();bool creating=options.Action==ImageAction.Archive7z;
        OpenPanel(creating?global::Orbit.Core.Localization.L.Get("Text_125E392A555A"):global::Orbit.Core.Localization.L.Get("Text_DC35CC1C20D4"),global::Orbit.Core.Localization.L.Format("Text_5CBEF238F2EA",(object?)(paths.Length)));
        int panelVersion=_comparisonVersion,loadVersion=_loadVersion;
        _panelContent.Children.Add(Text(creating?global::Orbit.Core.Localization.L.Get("Text_829F698BC78F"):global::Orbit.Core.Localization.L.Get("Text_C78EC51E61C7"),12));
        var password=AddSevenZipPasswordField(_panelContent,global::Orbit.Core.Localization.L.Get("Text_CB1A2074B3A0"),"sevenzip-password");
        var confirmation=creating?AddSevenZipPasswordField(_panelContent,global::Orbit.Core.Localization.L.Get("Text_65AF96D11B73"),"sevenzip-password-confirm"):null;
        var status=Text("",12,"#EDACB9");status.Tag="sevenzip-password-status";_panelContent.Children.Add(status);
        password.PasswordChanged+=(_,_)=>status.Text="";if(confirmation is not null)confirmation.PasswordChanged+=(_,_)=>status.Text="";
        bool Current()=>!_closing&&panelVersion==_comparisonVersion&&loadVersion==_loadVersion&&(!bindSelection||!_selectionLoading&&paths.SequenceEqual(_files));
        var run=Btn(creating?global::Orbit.Core.Localization.L.Get("Text_378D50E3CA50"):options.Action==ImageAction.Extract7z?global::Orbit.Core.Localization.L.Get("Text_047DD197C092"):global::Orbit.Core.Localization.L.Get("Text_A2414792DC0C"),(_,_)=>{
            if(!Current()){status.Text=global::Orbit.Core.Localization.L.Get("Text_E20C5A545788");return;}
            try{QueueSevenZipWithPassword(paths,options with{SevenZipPasswordProtected=true},ReadSevenZipPassword(password,confirmation));}
            catch(Exception ex){password.Clear();confirmation?.Clear();status.Text=ex.Message;password.Focus();}
        });GlassAccent(run);_panelContent.Children.Add(run);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_9101A5686FB1"),11));
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_8FBE9B75CBDF"),(_,_)=>{if(Current())ClosePanel();}));
        password.Focus();
    }
}
