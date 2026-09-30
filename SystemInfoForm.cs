using System.Data;

namespace SystemInfoGUI;

/// <summary>
/// 系统信息查询工具的主窗体。
/// </summary>
/// <remarks>
/// 职责有二，与采集侧严格分离：
/// <list type="bullet">
/// <item>界面构建与呈现——<see cref="InitializeComponent"/>、<see cref="AddRow"/>、<see cref="AddGroupTitle"/></item>
/// <item>采集编排——<see cref="LoadDataAsync"/>，负责把耗时采集移出 UI 线程并做异常兜底</item>
/// </list>
/// 分组规则完全来自 <see cref="SystemInfoCollector"/> 产出的分组列，
/// 本类不再依据属性名做字符串前缀匹配。
/// </remarks>
public class SystemInfoForm : Form
{
    private const string TitleText = "系统信息查询工具";

    private Button btnRefresh = null!;
    private DataGridView dataGridView = null!;

    public SystemInfoForm()
    {
        InitializeComponent();
        // Shown 时窗口已完成首次绘制，在此自动采集不会延迟窗口出现，也不会让用户看到空白表格。
        this.Shown += SystemInfoForm_Shown;
    }

    private async void SystemInfoForm_Shown(object? sender, EventArgs e)
    {
        // 启动后自动执行一次采集，与手动刷新共用同一条路径，不重复实现。
        await LoadDataAsync();
    }

    private void InitializeComponent()
    {
        this.Text = TitleText;
        this.Size = new System.Drawing.Size(1000, 750);
        this.StartPosition = FormStartPosition.CenterScreen;

        btnRefresh = new Button
        {
            Text = "刷新信息",
            Location = new System.Drawing.Point(10, 10),
            Size = new System.Drawing.Size(120, 30)
        };
        btnRefresh.Click += BtnRefresh_Click;

        dataGridView = new DataGridView
        {
            Location = new System.Drawing.Point(10, 50),
            Size = new System.Drawing.Size(960, 650),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            BackgroundColor = System.Drawing.Color.White,
            GridColor = System.Drawing.Color.LightGray,
            Font = new System.Drawing.Font("Segoe UI", 9)
        };
        dataGridView.Columns.Add("属性", "属性");
        dataGridView.Columns.Add("值", "值");
        dataGridView.Columns[0].Width = 200;
        dataGridView.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        this.Controls.Add(btnRefresh);
        this.Controls.Add(dataGridView);
    }

    private async void BtnRefresh_Click(object? sender, EventArgs e)
    {
        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        btnRefresh.Enabled = false;
        btnRefresh.Text = "采集中...";
        dataGridView.Rows.Clear();
        this.Text = $"{TitleText} - 正在采集";

        try
        {
            // WMI 查询（含 ASSOCIATORS OF 关联查询）、COM 调用与 powercfg 子进程均耗时较长，
            // 必须移出 UI 线程，否则界面会冻结数秒。
            DataTable table = await Task.Run(() => SystemInfoCollector.GetInfoTable());

            int infoCount = RenderGroups(table);
            this.Text = $"{TitleText} - 共 {infoCount} 项";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"获取数据失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            btnRefresh.Enabled = true;
            btnRefresh.Text = "刷新信息";
        }
    }

    /// <summary>
    /// 依据分组列渲染表格。
    /// </summary>
    /// <returns>实际信息条数，不含分组标题行。</returns>
    /// <remarks>
    /// 展示顺序遍历 <see cref="InfoGroup"/> 的枚举值，因此分组次序由枚举声明决定；
    /// 新增分组只需在枚举中追加，无需改动此处。
    /// </remarks>
    private int RenderGroups(DataTable table)
    {
        var buckets = new Dictionary<InfoGroup, List<DataRow>>();
        foreach (DataRow row in table.Rows)
        {
            InfoGroup group = ReadGroup(row);

            if (!buckets.TryGetValue(group, out List<DataRow>? rows))
            {
                rows = new List<DataRow>();
                buckets.Add(group, rows);
            }
            rows.Add(row);
        }

        int infoCount = 0;
        foreach (InfoGroup group in Enum.GetValues<InfoGroup>())
        {
            if (!buckets.TryGetValue(group, out List<DataRow>? rows) || rows.Count == 0)
                continue;

            // Other 组标题为空串，不显示分组标题行。
            string title = group.ToTitle();
            if (title.Length > 0)
                AddGroupTitle(title);

            foreach (DataRow row in rows)
            {
                AddRow(row["属性"]?.ToString() ?? "", row["值"]?.ToString() ?? "");
                infoCount++;
            }
        }

        return infoCount;
    }

    /// <summary>
    /// 从数据行读取分组。
    /// </summary>
    /// <remarks>
    /// <strong>注意 .NET DataTable 的存储行为</strong>：即便列声明为 <c>typeof(InfoGroup)</c>，
    /// DataTable 内部对枚举列按底层整数存储，<c>row["分组"]</c> 实际取出的是 <see cref="int"/>
    /// 而非枚举实例（实测 ToString() 返回 "5" 而不是 "Disk"）。因此不能直接用
    /// <c>is InfoGroup</c> 判定，否则所有行都会静默退到 <see cref="InfoGroup.Other"/>。
    /// 此处对两种取值形态都做兼容，分组列缺失或为空时退到 Other 而非丢弃该行。
    /// </remarks>
    private static InfoGroup ReadGroup(DataRow row)
    {
        object? raw = row["分组"];

        if (raw is InfoGroup direct)
            return direct;

        if (raw == null || raw is DBNull)
            return InfoGroup.Other;

        try
        {
            return (InfoGroup)Convert.ToInt32(raw);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            // 分组列内容异常时保持该行可见，归入默认区而不是丢弃。
            return InfoGroup.Other;
        }
    }

    private void AddRow(string prop, string value)
    {
        int rowIndex = dataGridView.Rows.Add(prop, value);
        dataGridView.Rows[rowIndex].DefaultCellStyle.BackColor = System.Drawing.Color.White;
    }

    private void AddGroupTitle(string title)
    {
        int rowIndex = dataGridView.Rows.Add(title, "");
        dataGridView.Rows[rowIndex].DefaultCellStyle.Font = new System.Drawing.Font("Segoe UI", 10, System.Drawing.FontStyle.Bold);
        dataGridView.Rows[rowIndex].DefaultCellStyle.BackColor = System.Drawing.Color.LightGray;
        dataGridView.Rows[rowIndex].DefaultCellStyle.ForeColor = System.Drawing.Color.DarkBlue;
        dataGridView.Rows[rowIndex].Cells[1].Style.BackColor = System.Drawing.Color.LightGray;
    }
}
