using System.Data;
using System.Drawing;

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
/// <para>
/// 表格为四列（属性/子项/项目/值），用于承载「网卡 → IPv4/IPv6 → IP地址/网关」这类层级数据；
/// 留空的单元格会被合并到上方或左侧的格子，从而呈现跨行、跨块的分组外观。
/// </para>
/// </remarks>
public class SystemInfoForm : Form
{
    private const string TitleText = "系统信息查询工具";

    // 单元格文字与格子边缘的间距，与 DataGridView 默认单元格的内边距观感保持一致。
    private const int CellTextPadding = 3;

    /// <summary>表格列数：属性（大类）/ 子项 / 项目 / 值。值恒在最后一列。</summary>
    private const int ColumnCount = 4;

    // 前三列收窄、把宽度让给值列；内容过长时靠换行完整呈现，不靠加宽截断。
    // 列宽下限要容得下本列最长文案（如次列的"屏幕关闭超时"）：行高由控件按单列宽度计算，
    // 若单列宽度不够就会误判为需要换行、把行撑高，而该格实际是跨列合并的、本可单行显示。
    private const int PropColumnWidth = 200;
    private const int SubColumnWidth = 85;
    private const int ItemColumnWidth = 80;

    private Button btnRefresh = null!;
    private DataGridView dataGridView = null!;

    /// <summary>
    /// 与表格行一一对应的元信息：所属分组、是否为分组标题行。
    /// </summary>
    /// <remarks>
    /// 合并只允许在同一分组的连续行内进行。缺少这层约束时，
    /// 后一组数据行的空列会被错误地并进前一组（例如磁盘行的空列并进上一张网卡）。
    /// </remarks>
    private readonly List<RowMeta> rowMetas = new();

    /// <summary>
    /// 每个单元格归属于哪个区域锚点（锚点即该区域左上角那个有内容的单元格）。
    /// <see langword="null"/> 表示该格无归属（整行皆空，正常情况下不会出现）。
    /// </summary>
    private (int Row, int Col)?[,] ownerMap = new (int Row, int Col)?[0, 0];

    /// <summary>锚点 → 区域尺寸（跨越的行数、列数）。</summary>
    private readonly Dictionary<(int Row, int Col), (int Rows, int Cols)> spanMap = new();

    private sealed class RowMeta
    {
        public InfoGroup Group { get; init; }
        public bool IsTitle { get; init; }
    }

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
            // 列宽由下方逐列指定：前三列固定宽，末列 Fill 吃掉剩余空间。
            // 若沿用控件级 Fill，会覆盖逐列设置，层级列的宽度就没法控制了。
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            // 列头行整体去掉：前三列在各分组里含义不同（网卡名/供电方式/属性名），列头既误导又占高度，
            // 分组信息已由各分组自己的标题行承载，这一行纯属冗余。
            ColumnHeadersVisible = false,
            BackgroundColor = System.Drawing.Color.White,
            GridColor = System.Drawing.Color.LightGray,
            Font = new System.Drawing.Font("Segoe UI", 9)
        };
        // 前三列在不同分组里承载的含义各不相同（网卡名 / 供电方式 / 属性名…），
        // 任何统一的列头文字都会误导，反而占掉宽度，所以只保留"值"这一列的列头。
        dataGridView.Columns.Add("colProp", string.Empty);
        dataGridView.Columns.Add("colSub", string.Empty);
        dataGridView.Columns.Add("colItem", string.Empty);
        dataGridView.Columns.Add("colValue", "值");
        dataGridView.Columns[0].Width = PropColumnWidth;
        dataGridView.Columns[1].Width = SubColumnWidth;
        dataGridView.Columns[2].Width = ItemColumnWidth;
        dataGridView.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        // 所有列都允许换行：列宽收窄后，长内容（网卡名、多地址、磁盘信息）必须靠换行
        // 完整呈现，截断等于丢信息。行高由 AllCells 随内容自适应。
        // 合并区域的高度按各行实际 Height 累加，行高不等也不会画错。
        dataGridView.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        dataGridView.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;

        // 排序与拖动列都会破坏界面结构：点击列头排序会把分组标题行和合并区域的行打乱，
        // 拖动列则会让各列的语义发生变化，合并绘制随即错位。两者一并禁用。
        for (int c = 0; c < ColumnCount; c++)
            dataGridView.Columns[c].SortMode = DataGridViewColumnSortMode.NotSortable;
        dataGridView.AllowUserToOrderColumns = false;
        dataGridView.CellPainting += DataGridView_CellPainting;
        dataGridView.CellFormatting += DataGridView_CellFormatting;

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
        ClearRows();
        this.Text = $"{TitleText} - 正在采集";

        try
        {
            // WMI 查询（含 ASSOCIATORS OF 关联查询）、COM 调用与 powercfg 子进程均耗时较长，
            // 必须移出 UI 线程，否则界面会冻结数秒。
            DataTable table = await Task.Run(() => SystemInfoCollector.GetInfoTable());

            RenderGroups(table);
            BuildMergeMap();
            // 标题固定为工具名，不再追加“共 N 项”——项数随采集结果变动，信息量低且显冗余。
            this.Text = TitleText;
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
    /// 清空表格以及与行绑定的全部派生状态。
    /// </summary>
    /// <remarks>
    /// 行索引失效后，元信息与归属表若不清空，自绘就会引用到已经不存在的行。
    /// </remarks>
    private void ClearRows()
    {
        dataGridView.Rows.Clear();
        rowMetas.Clear();
        spanMap.Clear();
        ownerMap = new (int Row, int Col)?[0, 0];
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
                AddGroupTitle(title, group);

            foreach (DataRow row in rows)
            {
                AddRow(
                    row["属性"]?.ToString() ?? "",
                    row["子项"]?.ToString() ?? "",
                    row["项目"]?.ToString() ?? "",
                    row["值"]?.ToString() ?? "",
                    group);
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

    /// <summary>
    /// 追加一行四列内容。留空的列会在 <see cref="BuildMergeMap"/> 中被合并到上方或左侧的格子。
    /// </summary>
    private void AddRow(string prop, string sub, string item, string value, InfoGroup group)
    {
        int rowIndex = dataGridView.Rows.Add(prop, sub, item, value);
        dataGridView.Rows[rowIndex].DefaultCellStyle.BackColor = System.Drawing.Color.White;
        rowMetas.Add(new RowMeta { Group = group, IsTitle = false });
    }

    /// <summary>
    /// 追加分组标题行。
    /// </summary>
    /// <remarks>
    /// 标题只写在首列，其余三列留空——它们会被合并进首列，使标题横跨整行。
    /// </remarks>
    private void AddGroupTitle(string title, InfoGroup group)
    {
        int rowIndex = dataGridView.Rows.Add(title, string.Empty, string.Empty, string.Empty);
        DataGridViewRow row = dataGridView.Rows[rowIndex];
        row.DefaultCellStyle.Font = new System.Drawing.Font("Segoe UI", 10, System.Drawing.FontStyle.Bold);
        row.DefaultCellStyle.BackColor = System.Drawing.Color.LightGray;
        row.DefaultCellStyle.ForeColor = System.Drawing.Color.DarkBlue;

        // 逐列补背景：标题行横跨整行，避免个别列露出白底。
        for (int c = 0; c < ColumnCount; c++)
            row.Cells[c].Style.BackColor = System.Drawing.Color.LightGray;

        rowMetas.Add(new RowMeta { Group = group, IsTitle = true });
    }

    /// <summary>
    /// 计算每个单元格的归属锚点与各锚点的区域尺寸。
    /// </summary>
    /// <remarks>
    /// DataGridView 没有 RowSpan/ColSpan 概念，一个单元格永远只有一行一列。
    /// 本方法用「留空即合并」的约定推导出区域，再由 <see cref="DataGridView_CellPainting"/> 自绘：
    /// <list type="bullet">
    /// <item>空单元格优先向上归附（同组连续行内最近的同列非空格），用于「网卡名」「IPv4」这类跨行延续；</item>
    /// <item>向上无果时向左归附（同行最近的非空格），用于普通行把中间空列吸收进属性列；</item>
    /// <item>锚点据此向下、向右延伸出实际区域。</item>
    /// </list>
    /// </remarks>
    private void BuildMergeMap()
    {
        int rows = dataGridView.Rows.Count;
        ownerMap = new (int Row, int Col)?[rows, ColumnCount];
        spanMap.Clear();

        // 第一遍：确定每个单元格的归属。
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < ColumnCount; c++)
            {
                ownerMap[r, c] = GetText(r, c).Length > 0
                    ? (r, c)                  // 自身有内容，即区域锚点
                    : ResolveEmptyCell(r, c); // 留空，向上或向左归附
            }
        }

        // 第二遍：由归属关系推出每个锚点覆盖多大区域。
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < ColumnCount; c++)
            {
                if (ownerMap[r, c] != (r, c))
                    continue; // 非锚点，无需计算

                int rowSpan = 1;
                while (r + rowSpan < rows
                       && GetText(r + rowSpan, c).Length == 0
                       && ownerMap[r + rowSpan, c] == (r, c))
                {
                    rowSpan++;
                }

                int colSpan = 1;
                while (c + colSpan < ColumnCount
                       && GetText(r, c + colSpan).Length == 0
                       && ownerMap[r, c + colSpan] == (r, c))
                {
                    colSpan++;
                }

                spanMap[(r, c)] = (rowSpan, colSpan);
            }
        }
    }

    /// <summary>
    /// 决定一个留空单元格归属哪个锚点，无法归附时返回 <see langword="null"/>。
    /// </summary>
    private (int Row, int Col)? ResolveEmptyCell(int row, int col)
    {
        // 纵向优先：向上承接到同组、最近的同列非空格（网卡名、IPv4 由此跨行）。
        // 遇到分组标题行或跨到别的分组立即停止，否则会把后一组的数据并进前一组。
        for (int r = row - 1; r >= 0; r--)
        {
            RowMeta upper = rowMetas[r];
            if (upper.IsTitle || upper.Group != rowMetas[row].Group)
                break;

            if (GetText(r, col).Length > 0)
                return (r, col);
        }

        // 横向兜底：并入同行左侧最近的非空格（普通行的中间空列由此被属性列吸收，
        // 于是呈现为「属性 | 值」的两段式外观，值占据右侧全部剩余宽度）。
        for (int c = col - 1; c >= 0; c--)
        {
            if (GetText(row, c).Length > 0)
                return (row, c);
        }

        return null;
    }

    /// <summary>
    /// 非最右列的短内容居中显示。
    /// </summary>
    /// <remarks>
    /// 「属性 / 子项 / 项目」这类短标签若靠左，与其右侧大片留白会显得偏沉一侧；
    /// 居中在格子里更均衡。判断标准是文本实测宽度不足所在列宽的七成——
    /// 长内容仍保持左对齐，扫读起点一致。最右的"值"列一律左对齐。
    /// CellFormatting 每次绘制都会触发，这里改的是当次绘制的样式副本，
    /// 恰好实现"按内容即时决定对齐"而不污染持久样式。
    /// </remarks>
    private void DataGridView_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.ColumnIndex == ColumnCount - 1)
            return;

        string text = e.Value?.ToString() ?? string.Empty;
        if (text.Length == 0)
            return;

        Size textSize = TextRenderer.MeasureText(text, dataGridView.Font);
        if (textSize.Width <= dataGridView.Columns[e.ColumnIndex].Width * 0.7)
            e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
    }

    /// <summary>
    /// 自绘需要合并的区域，使其在视觉上成为一个跨行、跨块的单一格子。
    /// </summary>
    /// <remarks>
    /// <strong>区域内每个单元格都重绘整块</strong>，而不是只画自己那一行/一列——
    /// 这样任意局部重绘（悬停、选中、滚动后的刷新）都能还原完整外观，
    /// 不会只剩半截文字或半条边框。区域内不画分隔线，只勾外框。
    /// 单格区域一律交回控件默认绘制，选中态等表现更原生。
    /// </remarks>
    private void DataGridView_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.RowIndex >= dataGridView.Rows.Count)
            return;

        // Graphics 在可空注解下标注为可空，取局部变量同时完成判空，后续绘制即可安全使用。
        Graphics? graphics = e.Graphics;
        if (graphics is null)
            return;

        (int Row, int Col)? anchor = ownerMap[e.RowIndex, e.ColumnIndex];
        if (anchor is null)
            return;

        (int ar, int ac) = anchor.Value;
        if (!spanMap.TryGetValue((ar, ac), out (int Rows, int Cols) span))
            return;

        if (span.Rows == 1 && span.Cols == 1)
            return; // 无需合并

        Rectangle spanRect = GetSpanRectangle(ar, ac, span.Rows, span.Cols);
        if (spanRect.Width <= 0 || spanRect.Height <= 0)
            return;

        // 交给自绘处理，阻止控件再画一遍默认外观（否则会在整块中间补回分隔线）。
        e.Handled = true;

        DataGridViewRow anchorRow = dataGridView.Rows[ar];
        bool selected = IsSpanSelected(ar, span.Rows);

        Color backColor = selected
            ? SelectionOrDefault(dataGridView.DefaultCellStyle.SelectionBackColor, SystemColors.Highlight)
            : CellOrDefault(anchorRow.DefaultCellStyle.BackColor, dataGridView.DefaultCellStyle.BackColor);
        Color foreColor = selected
            ? SelectionOrDefault(dataGridView.DefaultCellStyle.SelectionForeColor, SystemColors.HighlightText)
            : CellOrDefault(anchorRow.DefaultCellStyle.ForeColor, dataGridView.DefaultCellStyle.ForeColor);

        using (var brush = new SolidBrush(backColor))
            graphics.FillRectangle(brush, spanRect);

        string text = GetText(ar, ac);
        if (text.Length > 0)
        {
            Rectangle textRect = new(
                spanRect.Left + CellTextPadding,
                spanRect.Top,
                Math.Max(spanRect.Width - CellTextPadding * 2, 1),
                spanRect.Height);

            // 与 CellFormatting 的居中规则保持一致：区域不触及最右列且内容不足七成宽 → 居中。
            // 合并区域按整块宽度测量，而不是锚点单列的宽度，否则会误判为"占满"。
            bool touchesRightColumn = ac + span.Cols >= ColumnCount;
            TextFormatFlags align = TextFormatFlags.Left;
            if (!touchesRightColumn)
            {
                Size textSize = TextRenderer.MeasureText(text, anchorRow.DefaultCellStyle.Font ?? dataGridView.Font);
                if (textSize.Width <= spanRect.Width * 0.7)
                    align = TextFormatFlags.HorizontalCenter;
            }

            TextRenderer.DrawText(
                graphics,
                text,
                anchorRow.DefaultCellStyle.Font ?? dataGridView.Font,
                textRect,
                foreColor,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        // 只勾外框：区域内不画分隔线，合并后的格子才不会露出接缝。
        using var pen = new Pen(dataGridView.GridColor);
        graphics.DrawRectangle(pen, spanRect.X, spanRect.Y, spanRect.Width - 1, spanRect.Height - 1);
    }

    /// <summary>
    /// 读取指定单元格的文本，空值统一视为空串（判定合并时只关心"有没有内容"）。
    /// </summary>
    private string GetText(int rowIndex, int colIndex)
        => dataGridView.Rows[rowIndex].Cells[colIndex].Value?.ToString() ?? string.Empty;

    /// <summary>
    /// 计算合并区域在控件坐标系中的矩形。
    /// </summary>
    /// <remarks>
    /// 宽高按成员列宽、行高累加求得——列宽与行高都可被单独设置，不能按统一值估算。
    /// 结果再与控件客户区求交，避免区域半滚出可视区时画到列头或控件外。
    /// </remarks>
    private Rectangle GetSpanRectangle(int row, int col, int rowSpan, int colSpan)
    {
        // cutOverflow 传 false：取真实几何位置，截断交由下面的求交处理。
        int left = dataGridView.GetCellDisplayRectangle(col, row, cutOverflow: false).Left;
        int top = dataGridView.GetRowDisplayRectangle(row, cutOverflow: false).Top;

        int width = 0;
        for (int j = 0; j < colSpan; j++)
            width += dataGridView.Columns[col + j].Width;

        int height = 0;
        for (int i = 0; i < rowSpan; i++)
            height += dataGridView.Rows[row + i].Height;

        return Rectangle.Intersect(new Rectangle(left, top, width, height), dataGridView.ClientRectangle);
    }

    /// <summary>
    /// 合并区域内任一行被选中即整块高亮，避免出现「选中下半行而上半行不变色」的割裂观感。
    /// </summary>
    private bool IsSpanSelected(int startRow, int rowSpan)
    {
        for (int i = 0; i < rowSpan; i++)
        {
            if (dataGridView.Rows[startRow + i].Selected)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 选中态配色取不到时退回系统色。DataGridViewCellStyle 的颜色默认是
    /// <see cref="Color.Empty"/>，直接拿去画会得到透明/错误的颜色。
    /// </summary>
    private static Color SelectionOrDefault(Color value, Color fallback)
        => value.IsEmpty ? fallback : value;

    /// <summary>
    /// 普通态配色取不到时逐级退回：<see cref="Color.Empty"/> 不是合法绘制色。
    /// </summary>
    private static Color CellOrDefault(Color value, Color fallback)
        => value.IsEmpty ? (fallback.IsEmpty ? SystemColors.Window : fallback) : value;
}
