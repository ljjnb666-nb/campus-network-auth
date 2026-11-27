#if CHEZHI_LICENSED
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Chezhi.Licensing;

// All entrypoints are called on the WinForms thread; worker completion
// resumes there. No automatic enrollment, offline grant or destructive
// recovery. A card is persisted only after successful online validation.
internal sealed class DesktopLicenseGate : IDisposable
{
    private LicenseCoordinator coordinator;
    private bool closed,busy,activated;
    private int generation;
    private string localMessage;
    private byte[] pendingCard,pendingFingerprint;
    private readonly System.Diagnostics.Stopwatch clock=System.Diagnostics.Stopwatch.StartNew();
    private readonly LicenseRenewalSchedule renewal=new LicenseRenewalSchedule();
#if CHEZHI_LICENSE_UI_TEST
    private readonly Guid fixture;
    internal string FixtureCard;
    internal bool FixtureRemember,FixtureCancel,FixtureFirst;
    internal bool FixtureConfirmFirst=true;
    internal int FixtureDialogCount,FixtureConfirmationCount;
    internal DesktopLicenseGate(Guid id)
    {
        if(id==Guid.Empty)throw new ArgumentException("fixture");
        fixture=id;
    }
    internal void FixtureRenewDue()
    {
        renewal.Record(AuthorizationPhase.Authorized,clock.ElapsedMilliseconds-120000);
    }
#else
    internal DesktopLicenseGate() {}
#endif
    private DeviceEnrollment Enrollment()
    {
#if CHEZHI_LICENSE_UI_TEST
        return DeviceEnrollment.Fixture(fixture);
#else
        return new DeviceEnrollment();
#endif
    }
    private LicenseCardStore CardStore()
    {
#if CHEZHI_LICENSE_UI_TEST
        return LicenseCardStore.Fixture(fixture);
#else
        return new LicenseCardStore();
#endif
    }
    internal bool Busy {get{return busy;}}
    internal bool Allowed {get{return !closed && coordinator!=null && coordinator.CanStartProtectedOperation;}}
    internal void Invalidate()
    {
        generation++;
        renewal.Invalidate(clock.ElapsedMilliseconds);
        if(coordinator!=null){coordinator.NetworkChanged(false);coordinator.NetworkChanged(true);}
    }
    private static NativeLicenseClient CreateClient()
    {
        return new NativeLicenseClient(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"chezhi_license_sdk.dll"),
            CandidateTrust.DllHash,CandidateTrust.Origin,CandidateTrust.Scope,CandidateTrust.SigningKey,CandidateTrust.CA);
    }
    private void Reset()
    {
        if(coordinator!=null){coordinator.Dispose();coordinator=null;}
        activated=false;renewal.Reset();ClearPendingSave();
    }
    private void ClearPendingSave()
    {
        if(pendingCard!=null){Array.Clear(pendingCard,0,pendingCard.Length);pendingCard=null;}
        pendingFingerprint=null;
    }
    private async Task SaveAfterSuccessAsync(int epoch)
    {
        if(closed||epoch!=generation||!Allowed||pendingCard==null)return;
        // The worker owns independent bytes: Dispose/Reset can clear the
        // pending buffer without racing a DPAPI operation already begun.
        byte[] copy=(byte[])pendingCard.Clone(),fp=(byte[])pendingFingerprint.Clone();
        try
        {
            await Task.Run(delegate{CardStore().Save(copy,fp,CandidateTrust.Scope);});
            if(!closed&&epoch==generation)ClearPendingSave();
        }
        catch(Exception)
        {
            if(!closed&&epoch==generation)localMessage="授权已验证，但卡密保存失败，将在下次成功验证后重试。";
        }
        finally{Array.Clear(copy,0,copy.Length);}
    }
    private async Task<bool> CheckOnlineAsync(LicenseOperation op,int epoch)
    {
        Task<bool> call=coordinator.VerifyAsync(op);
        bool ok=call!=null&&await call;
        if(closed||epoch!=generation)return false;
        localMessage=null;
        renewal.Record(coordinator.Phase,clock.ElapsedMilliseconds);
        if(!ok||!Allowed)return false;
        activated=true;localMessage=null;
        await SaveAfterSuccessAsync(epoch);
        return !closed&&epoch==generation&&Allowed;
    }
    // Only the active-session timer calls this. No dialog, enrollment or
    // activation attempt. The state machine, not the timer, grants admission.
    internal async Task<bool> RenewIfDueAsync()
    {
        if(closed||busy||!activated||coordinator==null||!renewal.Due(clock.ElapsedMilliseconds))return false;
        busy=true;localMessage=null;
        int epoch=generation;
        try{await CheckOnlineAsync(LicenseOperation.Renew,epoch);return true;}
        catch(Exception)
        {
            if(!closed&&epoch==generation)renewal.Reset();
            return true;
        }
        finally{busy=false;}
    }
    private sealed class Loaded : IDisposable
    {
        internal WindowsDeviceKey Key;
        internal byte[] Card;
        public void Dispose()
        {if(Key!=null)Key.Dispose();if(Card!=null)Array.Clear(Card,0,Card.Length);}
    }
    private async Task<bool> LoadSavedAsync(int epoch)
    {
        Loaded saved=await Task.Run(delegate{
            var result=new Loaded();
            try
            {
                result.Key=Enrollment().OpenExisting();
                result.Card=CardStore().Load(result.Key.Fingerprint,CandidateTrust.Scope);
                return result;
            }
            catch{result.Dispose();throw;}
        });
        using(saved)
        {
            if(closed||epoch!=generation||saved.Card==null)return false;
            coordinator=new LicenseCoordinator(CreateClient(),saved.Key,saved.Card);
            saved.Key=null;activated=true;
            return true;
        }
    }

    // Explicit card replacement never inherits the previous grant. Existing
    // saved card is left intact until a newly supplied card verifies and
    // the user explicitly requests saving it.
    internal async Task<bool> VerifyAsync(IWin32Window owner,bool interactive,bool replace=false)
    {
        if(closed||busy)return false;
        busy=true;localMessage=null;
        int epoch=generation;
        try
        {
            if(replace&&!interactive)return false;
            if(coordinator==null&&!replace)
            {
                try{await LoadSavedAsync(epoch);}
                catch(Exception){localMessage="已保存卡密或设备记录不可用，原记录未改动。请核对设备身份或手动输入卡密。";}
                if(closed||epoch!=generation)return false;
            }
            if(coordinator==null||replace)
            {
                if(!interactive)return false;
                using(var dialog=new ActivationDialog())
                {
#if CHEZHI_LICENSE_UI_TEST
                    dialog.Shown+=delegate{
                        FixtureDialogCount++;
                        dialog.FixtureConfirmEnrollment=delegate{FixtureConfirmationCount++;return FixtureConfirmFirst;};
                        dialog.BeginInvoke((Action)delegate{dialog.FixtureFill(FixtureCard,FixtureRemember,FixtureCancel,FixtureFirst);});
                    };
#endif
                    if(dialog.ShowDialog(owner)!=DialogResult.OK||closed||epoch!=generation)return false;
                    byte[] card;
                    if(!LicenseCardInput.TryNormalize(dialog.Card,out card))return false;
                    // Cancel or malformed input does not destroy the
                    // prior context. Only confirmed valid replacement does.
                    if(replace)Reset();
                    try
                    {
                        var sdk=CreateClient();
                        bool first=dialog.FirstEnrollment,remember=dialog.Remember;
                        DeviceKeyPolicy policy=dialog.TpmRequired?DeviceKeyPolicy.TpmRequired:DeviceKeyPolicy.Software;
                        WindowsDeviceKey key=await Task.Run(delegate{
                            var enrollment=Enrollment();
                            return first?enrollment.EnrollFirst(policy):enrollment.OpenExisting();
                        });
                        if(closed||epoch!=generation){key.Dispose();return false;}
                        byte[] fingerprint=key.Fingerprint;
                        try{coordinator=new LicenseCoordinator(sdk,key,card);}
                        catch{key.Dispose();throw;}
                        if(remember){pendingCard=(byte[])card.Clone();pendingFingerprint=fingerprint;}
                        return await CheckOnlineAsync(LicenseOperation.Activate,epoch);
                    }
                    finally{Array.Clear(card,0,card.Length);dialog.ClearCard();}
                }
            }
            return await CheckOnlineAsync(activated?LicenseOperation.Validate:LicenseOperation.Activate,epoch);
        }
        catch(Exception){return false;}
        finally{busy=false;}
    }
    internal async Task<bool> ForgetAsync()
    {
        if(closed||busy)return false;
        busy=true;Reset();
        try{await Task.Run(delegate{CardStore().Forget();});localMessage="已清除本地卡密，设备身份未删除。";return !closed;}
        catch(Exception){localMessage="清除本地卡密失败，请稍后重试。";return false;}
        finally{busy=false;}
    }
    internal string Message
    {
        get
        {
            if(closed)return "授权已关闭。";
            if(busy)return "授权操作正在进行，请稍后。";
            if(localMessage!=null)return localMessage;
            if(coordinator==null)return "未完成授权，请核对卡密、设备身份及可信配置。";
            switch(coordinator.Phase)
            {
                case AuthorizationPhase.Authorized:return "当前授权验证通过。";
                case AuthorizationPhase.NetworkUnavailable:return "授权服务暂时不可达，请先通过官方客户端或热点联网后重试。";
                case AuthorizationPhase.RetryLater:return "授权服务繁忙，请稍后重试。";
                case AuthorizationPhase.Denied:return "授权请求被拒绝，请核对卡密与设备绑定。";
                case AuthorizationPhase.SecurityError:return "授权响应未通过安全校验。";
                case AuthorizationPhase.Expired:return "当前授权已到期，需要重新在线验证。";
                default:return "尚未取得有效在线授权，请重试。";
            }
        }
    }
    public void Dispose(){closed=true;generation++;Reset();}
    private sealed class ActivationDialog : Form
    {
        private readonly TextBox card=new TextBox();
        private readonly CheckBox enroll=new CheckBox(),tpm=new CheckBox(),remember=new CheckBox();
        internal bool Remember {get{return remember.Checked;}}
        internal string Card {get{return card.Text.Trim();}}
        internal bool FirstEnrollment {get{return enroll.Checked;}}
        internal bool TpmRequired {get{return tpm.Checked;}}
        internal void ClearCard(){card.Clear();}
#if CHEZHI_LICENSE_UI_TEST
        // Test-only response to the confirmation prompt. The production
        // binary never contains this delegate or the scripted UI input.
        internal Func<bool> FixtureConfirmEnrollment;
        internal void FixtureFill(string value,bool save,bool cancel,bool first)
        {
            card.Text=value;remember.Checked=save;enroll.Checked=first;
            if(cancel){CancelButton.PerformClick();return;}
            AcceptButton.PerformClick();
            // Declining a confirmation should leave the dialog open.
            // Close it via the actual cancel button to finish the test.
            if(DialogResult!=DialogResult.OK&&!IsDisposed)CancelButton.PerformClick();
        }
#endif
        internal ActivationDialog()
        {
            Text="澈知科技 · 软件授权";
            StartPosition=FormStartPosition.CenterParent;
            AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Microsoft YaHei UI",10F);
            ClientSize=new Size(470,300);MinimumSize=new Size(420,300);
            MaximizeBox=false;MinimizeBox=false;
            var panel=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=7};
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label{Text="请输入授权卡密",AutoSize=true},0,0);
            card.UseSystemPasswordChar=true;card.MaxLength=100;card.Dock=DockStyle.Top;
            panel.Controls.Add(card,0,1);
            enroll.Text="首次登记此 Windows 用户的设备身份";enroll.AutoSize=true;
            tpm.Text="要求 TPM 硬件保护（不可用时不降级）";tpm.AutoSize=true;tpm.Enabled=false;
            enroll.CheckedChanged+=delegate{tpm.Enabled=enroll.Checked;if(!enroll.Checked)tpm.Checked=false;};
            panel.Controls.Add(enroll,0,2);panel.Controls.Add(tpm,0,3);
            remember.Text="在线验证成功后加密保存卡密";remember.AutoSize=true;
            panel.Controls.Add(remember,0,4);
            panel.Controls.Add(new Label{Text="已有设备不要勾选首次登记。请先通过官方客户端或热点联网。设备丢失需人工恢复。",Dock=DockStyle.Fill,AutoSize=false},0,5);
            var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,FlowDirection=FlowDirection.RightToLeft};
            var ok=new Button{Text="验证授权",AutoSize=true};var cancel=new Button{Text="取消",DialogResult=DialogResult.Cancel,AutoSize=true};
            ok.Click+=delegate{
                byte[] normalized;
                if(!LicenseCardInput.TryNormalize(card.Text,out normalized))
                {
                    MessageBox.Show(this,"请输入完整的 25 位授权卡密。","软件授权",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                    return;
                }
                Array.Clear(normalized,0,normalized.Length);
                if(enroll.Checked)
                {
#if CHEZHI_LICENSE_UI_TEST
                    if(FixtureConfirmEnrollment==null||!FixtureConfirmEnrollment())return;
#else
                    if(MessageBox.Show(this,"确认这是首次登记？已有设备身份不得重新登记。","设备登记",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
#endif
                }
                DialogResult=DialogResult.OK;Close();
            };
            actions.Controls.Add(ok);actions.Controls.Add(cancel);panel.Controls.Add(actions,0,6);
            Controls.Add(panel);AcceptButton=ok;CancelButton=cancel;
        }
    }
}
#endif
