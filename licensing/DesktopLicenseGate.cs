#if CHEZHI_LICENSED
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Chezhi.Licensing;

// Candidate integration. No card persistence or offline bypass. Only the
// explicit activation dialog may enroll; a normal verification never does.
internal sealed class DesktopLicenseGate : IDisposable
{
    private LicenseCoordinator coordinator;
    private bool closed, busy;
    internal bool Allowed {get{return !closed && coordinator!=null && coordinator.CanStartProtectedOperation;}}
    internal void Invalidate()
    {
        if(coordinator!=null) {coordinator.NetworkChanged(false);coordinator.NetworkChanged(true);}
    }
    internal async Task<bool> VerifyAsync(IWin32Window owner,bool interactive)
    {
        if(closed||busy)return false;
        busy=true;
        try
        {
            if(coordinator==null)
            {
                if(!interactive)return false;
                using(var dialog=new ActivationDialog())
                {
                    if(dialog.ShowDialog(owner)!=DialogResult.OK||closed)return false;
                    byte[] card=Encoding.UTF8.GetBytes(dialog.Card);
                    try
                    {
                        if(card.Length==0||card.Length>100)return false;
                        var sdk=new NativeLicenseClient(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"chezhi_license_sdk.dll"),
                            CandidateTrust.DllHash,CandidateTrust.Origin,CandidateTrust.Scope,CandidateTrust.SigningKey,CandidateTrust.CA);
                        // Enrollment can perform registry and CNG operations; do
                        // not run on the UI thread. The explicit user choice
                        // is carried into the worker; no fallback on failure.
                        bool first=dialog.FirstEnrollment;
                        DeviceKeyPolicy policy=dialog.TpmRequired?DeviceKeyPolicy.TpmRequired:DeviceKeyPolicy.Software;
                        WindowsDeviceKey key=await Task.Run(delegate {
                            var enrollment=new DeviceEnrollment();
                            return first?enrollment.EnrollFirst(policy):enrollment.OpenExisting();
                        });
                        if(closed){key.Dispose();return false;}
                        try {coordinator=new LicenseCoordinator(sdk,key,card);}
                        catch {key.Dispose();throw;}
                    }
                    finally {Array.Clear(card,0,card.Length);dialog.ClearCard();}
                }
                // Activation is safe to retry with the same enrolled key.
                // This initial request may activate or confirm an existing binding.
                Task<bool> activated=coordinator.VerifyAsync(LicenseOperation.Activate);
                return activated!=null && await activated && Allowed;
            }
            Task<bool> verified=coordinator.VerifyAsync(LicenseOperation.Activate);
            return verified!=null && await verified && Allowed;
        }
        catch(Exception) {return false;}
        finally {busy=false;}
    }

    internal string Message
    {
        get
        {
            if(closed)return "授权已关闭。";
            if(coordinator==null)return "未完成授权或设备登记不可用，请核对卡密、设备身份及可信配置。";
            switch(coordinator.Phase)
            {
                case AuthorizationPhase.NetworkUnavailable:return "授权服务暂时不可达，请先使用已授权的网络或官方客户端联网后重试。";
                case AuthorizationPhase.RetryLater:return "授权服务繁忙，请稍后重试。";
                case AuthorizationPhase.Denied:return "授权请求被拒绝，请核对卡密与设备绑定。";
                case AuthorizationPhase.SecurityError:return "授权响应未通过安全校验。";
                default:return "尚未取得有效在线授权，请重试。";
            }
        }
    }
    public void Dispose()
    {
        closed=true;
        if(coordinator!=null){coordinator.Dispose();coordinator=null;}
    }

    private sealed class ActivationDialog : Form
    {
        private readonly TextBox card=new TextBox();
        private readonly CheckBox enroll=new CheckBox(),tpm=new CheckBox();
        internal string Card {get{return card.Text.Trim();}}
        internal bool FirstEnrollment {get{return enroll.Checked;}}
        internal bool TpmRequired {get{return tpm.Checked;}}
        internal void ClearCard(){card.Clear();}
        internal ActivationDialog()
        {
            Text="澈知科技 · 软件授权";
            StartPosition=FormStartPosition.CenterParent;
            AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Microsoft YaHei UI",10F);
            ClientSize=new Size(460,260);MinimumSize=new Size(420,260);
            MaximizeBox=false;MinimizeBox=false;
            var panel=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=6};
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label{Text="请输入授权卡密（本次运行内使用）",AutoSize=true},0,0);
            card.UseSystemPasswordChar=true;card.MaxLength=100;card.Dock=DockStyle.Top;
            panel.Controls.Add(card,0,1);
            enroll.Text="首次登记此 Windows 用户的设备身份";enroll.AutoSize=true;
            tpm.Text="要求 TPM 硬件保护（不可用时不降级）";tpm.AutoSize=true;tpm.Enabled=false;
            enroll.CheckedChanged+=delegate{tpm.Enabled=enroll.Checked;if(!enroll.Checked)tpm.Checked=false;};
            panel.Controls.Add(enroll,0,2);panel.Controls.Add(tpm,0,3);
            panel.Controls.Add(new Label{Text="已有设备不要勾选首次登记。请先通过官方客户端或热点联网。设备丢失需人工恢复。",Dock=DockStyle.Fill,AutoSize=false},0,4);
            var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,FlowDirection=FlowDirection.RightToLeft};
            var ok=new Button{Text="验证授权",AutoSize=true};var cancel=new Button{Text="取消",DialogResult=DialogResult.Cancel,AutoSize=true};
            ok.Click+=delegate{
                if(String.IsNullOrWhiteSpace(card.Text))return;
                if(enroll.Checked&&MessageBox.Show(this,"确认这是首次登记？已有设备身份不得重新登记。","设备登记",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
                DialogResult=DialogResult.OK;Close();
            };
            actions.Controls.Add(ok);actions.Controls.Add(cancel);panel.Controls.Add(actions,0,5);
            Controls.Add(panel);AcceptButton=ok;CancelButton=cancel;
        }
    }
}
#endif
