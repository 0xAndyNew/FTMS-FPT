using System.Text.Json;
using FTMS.Application;
using FTMS.Domain;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
namespace FTMS.Desktop;

public sealed class WebViewFtmsClient(WebView2 webView, string ftmsUrl) : IFtmsClient
{
    private readonly WebViewLoginRecovery _loginRecovery = new(webView, new Uri(ftmsUrl));
    private readonly SemaphoreSlim _identityLock = new(1, 1);
    private CurrentUserIdentity? _currentUser;
    private int _identityGeneration;

    internal event Action<LoginRecoveryStatus>? LoginRecoveryStatusChanged
    {
        add => _loginRecovery.StatusChanged += value;
        remove => _loginRecovery.StatusChanged -= value;
    }

    public async Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken)
    {
        var uri = await webView.Dispatcher.InvokeAsync(() => webView.Source).Task.WaitAsync(cancellationToken);
        var authenticated = uri is not null && WebViewLoginRecovery.IsFtmsIhubUri(uri);
        if (!authenticated) InvalidateCurrentUser();
        return authenticated;
    }

    public async Task<CurrentUserIdentity?> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (_currentUser is not null) return _currentUser;
        await _identityLock.WaitAsync(cancellationToken);
        try
        {
            if (_currentUser is not null) return _currentUser;
            var generation = _identityGeneration;
            foreach (var delay in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(750) })
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                var json = await ExecuteJsonStringAsync(CurrentUserScript);
                var identity = DeserializeCurrentUser(json);
                if (identity is null) continue;
                if (generation == _identityGeneration) _currentUser = identity;
                return generation == _identityGeneration ? identity : null;
            }
            return null;
        }
        finally { _identityLock.Release(); }
    }

    public Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(CancellationToken cancellationToken) =>
        GetTicketsAsync(includeHistory: false, cancellationToken);

    public async Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(bool includeHistory, CancellationToken cancellationToken)
    {
        var script = BuildTicketsScript(includeActive: true, includeHistory: includeHistory);
        return await ExecuteTicketsQueryAsync(script, cancellationToken);
    }

    public async Task<IReadOnlyList<TicketSnapshot>> GetClosedTicketsAsync(CancellationToken cancellationToken)
    {
        var script = BuildTicketsScript(includeActive: false, includeHistory: true);
        return await ExecuteTicketsQueryAsync(script, cancellationToken);
    }

    private async Task<IReadOnlyList<TicketSnapshot>> ExecuteTicketsQueryAsync(string script, CancellationToken cancellationToken)
    {
        var json = await ExecuteAsyncJsonStringAsync(script, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.String)
        {
            using var nested = JsonDocument.Parse(root.GetString() ?? "null");
            root = nested.RootElement.Clone();
        }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
        {
            var message = error.GetString() ?? "Không thể đọc API FTMS";
            if (message.Contains("401", StringComparison.OrdinalIgnoreCase) || message.Contains("403", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(message);
            throw new InvalidOperationException(message);
        }
        return DeserializeTickets(root.GetRawText());
    }

    private static string BuildTicketsScript(bool includeActive, bool includeHistory)
    {
        var activeFlag = includeActive ? "true" : "false";
        var historyFlag = includeHistory ? "true" : "false";
        return $$"""
            (async () => {
              const unwrap = (value, depth = 0) => {
                if (depth > 8 || value == null) return [];
                if (Array.isArray(value)) return value;
                if (typeof value === 'string') {
                  try { return unwrap(JSON.parse(value), depth + 1); } catch { return []; }
                }
                if (typeof value !== 'object') return [];
                for (const key of ['data','Data','rows','Rows','items','Items','result','Result']) {
                  const rows = unwrap(value[key], depth + 1);
                  if (rows.length) return rows;
                }
                for (const child of Object.values(value)) {
                  const rows = unwrap(child, depth + 1);
                  if (rows.some(x => x && typeof x === 'object')) return rows;
                }
                return [];
              };
              const pick = (row, ...keys) => {
                for (const key of keys) if (row?.[key] !== undefined && row[key] !== null && row[key] !== '') return row[key];
                return null;
              };
              const statusMap = { 'mới': 0, 'tạo mới': 0, 'new': 0, 'phân công': 1, 'assigned': 1,
                'đang thực hiện': 2, 'in progress': 2, 'hoàn thành': 3, 'completed': 3,
                'tạm ngưng': 4, 'paused': 4, 'đóng': 5, 'đã đóng': 5, 'closed': 5,
                'hủy': 7, 'đã hủy': 7, 'cancelled': 7, 'không xử lý': 8 };
              const claimIdOf = row => pick(row, 'assignId','assignID','assignmentId','AssignmentId','assignmentID',
                'requestAssignId','RequestAssignId','requestAssignID','strID','intID','requestID','RequestID','requestId',
                'id','Id','ID','ticketId','TicketId');
              const statusOf = row => {
                const raw = pick(row, 'status','Status','statusId','StatusId','STATUS_ID','STATUSID','statusID');
                if (typeof raw === 'number') return raw;
                if (raw && typeof raw === 'object') return Number(pick(raw, 'id','Id','value','Value') ?? 0);
                const numeric = Number(raw);
                if (raw !== null && raw !== '' && Number.isFinite(numeric)) return numeric;
                const name = String(pick(row, 'statusName','StatusName','STATUS_NAME','statusText','StatusText', 'status') || '')
                  .trim().toLocaleLowerCase('vi-VN');
                return statusMap[name] ?? null;
              };
              const dateOf = value => {
                if (!value) return null;
                if (typeof value === 'string') {
                  const vi = value.match(/(\d{1,2}):(\d{2})(?::(\d{2}))?\s*-?\s*(\d{1,2})\/(\d{1,2})\/(\d{4})/);
                  if (vi) return `${vi[6]}-${vi[5].padStart(2,'0')}-${vi[4].padStart(2,'0')}T${vi[1].padStart(2,'0')}:${vi[2]}:${vi[3] || '00'}+07:00`;
                  const viDate = value.match(/^(\d{1,2})\/(\d{1,2})\/(\d{4})$/);
                  if (viDate) return `${viDate[3]}-${viDate[2].padStart(2,'0')}-${viDate[1].padStart(2,'0')}T00:00:00+07:00`;
                  const dotNet = value.match(/\/Date\((\d+)(?:[+-]\d+)?\)\//);
                  if (dotNet) return new Date(Number(dotNet[1])).toISOString();
                  if (/^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?$/.test(value))
                    return value.replace(' ', 'T') + '+07:00';
                }
                const parsed = new Date(value);
                return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
              };
              const numberOf = value => {
                if (value === null || value === undefined || value === '') return null;
                const parsed = Number(value);
                return Number.isFinite(parsed) ? parsed : null;
              };
              const emailOf = row => {
                const extract = value => String(value || '').match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/ig) || [];
                const keys = ['mailPoster','MailPoster','posterEmail','PosterEmail','poster','Poster',
                  'senderEmail','SenderEmail','sender','Sender','emailFrom','EmailFrom','fromEmail','FromEmail',
                  'fromAddress','FromAddress','mailFrom','MailFrom'];
                const ignored = ['fti.sd02@fpt.com','ihub.akabot2','ducvm19@fpt.com'];
                if (keys.some(key => ignored.some(value => String(row?.[key] || '').toLowerCase().includes(value)))) return null;
                const sender = keys.map(key => extract(row?.[key])[0]).find(Boolean) || null;
                return sender;
              };

              const fetchActiveGroup = async (strStatus, isMyTicket) => {
                const rows = [];
                const pageSize = 1000;
                for (let page = 1; page <= 5; page++) {
                  const body = new URLSearchParams({ take: String(pageSize), skip: String((page - 1) * pageSize),
                    page: String(page), pageSize: String(pageSize), search: '', isMyTicket: String(isMyTicket),
                    isAkabot: 'false', strStatus, strRegionID: '0,2,1', linkDeptId: '', alarmType: '0',
                    isSortByDate: 'true' });
                  const response = await fetch('/ihub/request/GetListRequestV12', {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
                      'X-Requested-With': 'XMLHttpRequest' },
                    body: body.toString()
                  });
                  if (/\/id\/login|\/adfs\//i.test(new URL(response.url).pathname))
                    throw new Error('FTMS API HTTP 401');
                  if (!response.ok) throw new Error(`FTMS API HTTP ${response.status}`);
                  const pageRows = unwrap(await response.json());
                  rows.push(...pageRows);
                  if (pageRows.length < pageSize) break;
                }
                return rows;
              };

              const fetchActive = async () => {
                const scopes = [
                  ['1', false], ['2,4', false],
                  ['1', true], ['2,4', true]
                ];
                const results = await Promise.allSettled(scopes.map(([statuses, mine]) =>
                  fetchActiveGroup(statuses, mine)));
                const rows = [];
                const errors = [];
                let anySuccess = false;
                for (const result of results) {
                  if (result.status === 'fulfilled') {
                    anySuccess = true;
                    rows.push(...result.value);
                  } else {
                    errors.push(String(result.reason?.message || result.reason || ''));
                  }
                }
                if (!anySuccess) throw new Error(errors.join('; ') || 'Không thể đọc danh sách ticket FTMS');

                const tickets = rows.map(row => ({
                  code: String(pick(row, 'code','Code','requestCode','RequestCode','REQUEST_CODE','REQUESTCODE','requestNo','RequestNo') || '').trim(),
                  status: statusOf(row),
                  title: pick(row, 'title','Title','subject','Subject','REQUEST_TITLE'),
                  createdAt: dateOf(pick(row, 'createDate','CreateDate','createdAt','CreatedAt','CREATE_DATE')),
                  updatedAt: dateOf(pick(row, 'updateDate','UpdateDate','updatedAt','UpdatedAt','modifyDate','ModifyDate','lastUpdate','LastUpdate')),
                  updatedBy: pick(row, 'updatedBy','UpdatedBy','updateBy','UpdateBy','modifiedBy','ModifiedBy','lastUpdateBy','LastUpdateBy','updateStaffName','UpdateStaffName'),
                  assigneeId: numberOf(pick(row, 'staffId','StaffId','agentId','AgentId','ASSIGNEE_ID')),
                  assigneeName: pick(row, 'agentName','AgentName','staffName','StaffName','assigneeName','AssigneeName'),
                  departmentId: numberOf(pick(row, 'deptId','DeptId','departmentId','DepartmentId','DEPARTMENT_ID')),
                  departmentName: pick(row, 'department','Department','departmentName','DepartmentName','deptName','DeptName'),
                  slaDeviationMinutes: numberOf(pick(row, 'slaDeviation','SlaDeviation','SLA_DEVIATION','slaDeviationMinutes','SlaDeviationMinutes')),
                  slaType: numberOf(pick(row, 'typeSLA','TypeSLA','typeSla','slaType','SlaType','SLA_TYPE')),
                  latestEmail: (() => {
                    const sender = emailOf(row);
                    const subject = pick(row, 'emailSubject','EmailSubject');
                    const content = pick(row, 'emailContent','EmailContent');
                    const sentAt = dateOf(pick(row, 'emailDate','EmailDate','sendDate','SendDate','sentAt','SentAt'));
                    if (!sender || !sentAt) return null;
                    const body = String(content || '').trim() === String(subject || '').trim() ? null : content;
                    return { id: String(pick(row, 'emailHistoryId','EmailHistoryId','emailId','EmailId','id','Id') || ''),
                      sentAt, from: sender, subject, body };
                  })()
                })).filter(x => x.code && x.status !== null);

                const completeness = ticket => [ticket.title, ticket.assigneeId, ticket.assigneeName,
                  ticket.departmentId, ticket.departmentName, ticket.latestEmail].filter(Boolean).length;
                const byCode = new Map();
                for (const ticket of tickets) {
                  const key = ticket.code.toLocaleUpperCase('vi-VN');
                  const current = byCode.get(key);
                  const candidateTime = ticket.updatedAt ? new Date(ticket.updatedAt).getTime() : Number.NEGATIVE_INFINITY;
                  const currentTime = current?.updatedAt ? new Date(current.updatedAt).getTime() : Number.NEGATIVE_INFINITY;
                  if (!current || candidateTime > currentTime ||
                      candidateTime === currentTime && completeness(ticket) > completeness(current))
                    byCode.set(key, ticket);
                }

                if (!globalThis.__ftmsTicketCache) globalThis.__ftmsTicketCache = new Map();
                for (const row of rows) {
                  const rowCode = String(pick(row, 'code','Code','requestCode','RequestCode','REQUEST_CODE','REQUESTCODE','requestNo','RequestNo') || '').trim();
                  const rowClaimId = claimIdOf(row);
                  if (rowCode && rowClaimId) {
                    globalThis.__ftmsTicketCache.set(rowCode.toLocaleUpperCase('vi-VN'), {
                      id: String(rowClaimId).trim(),
                      status: statusOf(row),
                      owner: numberOf(pick(row, 'staffId','StaffId','agentId','AgentId','assigneeId','AssigneeId','ASSIGNEE_ID'))
                    });
                  }
                }

                return Array.from(byCode.values());
              };

              const fetchHistory = async () => {
                const userDept = (typeof globalThis.UserDept !== 'undefined' && globalThis.UserDept !== null && String(globalThis.UserDept).trim() !== '' && String(globalThis.UserDept) !== '0')
                  ? String(globalThis.UserDept)
                  : '-1';
                const vietnamDate = offsetDays => {
                  const date = new Date(Date.now() + (7 * 60 * 60 * 1000) + offsetDays * 86400000);
                  return `${String(date.getUTCMonth() + 1).padStart(2, '0')}/${String(date.getUTCDate()).padStart(2, '0')}/${date.getUTCFullYear()}`;
                };

                const fetchStatusHistory = async (historyStatus) => {
                  const list = [];
                  const seenPageKeys = new Set();
                  for (let page = 1; page <= 2; page++) {
                    const historyParams = new URLSearchParams({
                      searchData: JSON.stringify({ search: '', source: '-1', departmentId: userDept, staffId: '-1',
                        serviceTypeGroup: '-1', serviceType: '-1', requestLevel: '-1', status: historyStatus }),
                      fromDate: vietnamDate(-1), toDate: vietnamDate(1), typeSearch: '0',
                      take: '1000', skip: String((page - 1) * 1000), page: String(page), pageSize: '1000'
                    });
                    const historyResponse = await fetch('/ihub/list/GetListHistoryRequestByType?' + historyParams.toString(), {
                      credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' }
                    });
                    if (/\/id\/login|\/adfs\//i.test(new URL(historyResponse.url).pathname))
                      throw new Error('FTMS history HTTP 401');
                    if (!historyResponse.ok) throw new Error(`FTMS history HTTP ${historyResponse.status}`);
                    const historyPayload = await historyResponse.json();
                    const historyRows = unwrap(historyPayload);
                    if (!Array.isArray(historyRows)) throw new Error('FTMS history JSON không có danh sách dữ liệu');
                    if (!historyRows.length) break;
                    const pageKey = historyRows.map(row => String(pick(row, 'id','Id','code','Code','requestCode','RequestCode') || '')).join('|');
                    if (seenPageKeys.has(pageKey)) break;
                    seenPageKeys.add(pageKey);
                    for (const row of historyRows) {
                      const code = String(pick(row, 'code','Code','requestCode','RequestCode','REQUEST_CODE') || '').trim();
                      const explicitClosedAt = pick(row, 'closeDate','CloseDate','closedAt','ClosedAt','closedDate','ClosedDate',
                        'completionDate','CompletionDate');
                      const rowStatus = pick(row, 'status','Status','statusId','StatusId');
                      const rowStatusName = String(pick(row, 'statusName','StatusName','statusText','StatusText') || '')
                        .trim().toLocaleLowerCase('vi-VN');
                      const isClosedRow = explicitClosedAt || Number(rowStatus) === 5 ||
                        ['đóng','đã đóng','closed'].includes(rowStatusName);
                      const closedAt = isClosedRow ? dateOf(explicitClosedAt ??
                        pick(row, 'updateDate','UpdateDate','updatedAt','UpdatedAt')) : null;
                      if (!code || !closedAt) continue;
                      const closedByName = pick(row, 'closedByName','ClosedByName','closedBy','ClosedBy','userName','UserName',
                        'updatedBy','UpdatedBy','closeUserName','CloseUserName','staffName','StaffName','agentName','AgentName');
                      list.push({
                        code, status: 5,
                        title: pick(row, 'title','Title','subject','Subject','REQUEST_TITLE'),
                        createdAt: dateOf(pick(row, 'createDate','CreateDate','createdAt','CreatedAt','CREATE_DATE')),
                        updatedAt: closedAt, closedAt,
                        updatedBy: closedByName,
                        closedByUserId: numberOf(pick(row, 'closedByUserId','ClosedByUserId','closedById','ClosedById',
                          'closeUserId','CloseUserId','userId','UserId','USER_ID','staffId','StaffId','agentId','AgentId')),
                        closedByName,
                        assigneeId: numberOf(pick(row, 'assigneeId','AssigneeId','assignedStaffId','AssignedStaffId','ASSIGNEE_ID')),
                        assigneeName: pick(row, 'assigneeName','AssigneeName','assignedStaffName','AssignedStaffName'),
                        departmentId: numberOf(pick(row, 'departmentId','DepartmentId','deptId','DeptId')),
                        departmentName: pick(row, 'departmentName','DepartmentName','department','Department'),
                        slaDeviationMinutes: null, slaType: null
                      });
                    }
                    if (historyRows.length < 1000) break;
                  }
                  return list;
                };

                const results = await Promise.allSettled(['6', '5'].map(fetchStatusHistory));
                const closed = [];
                let anySuccess = false;
                const errors = [];
                for (let i = 0; i < results.length; i++) {
                  const r = results[i];
                  if (r.status === 'fulfilled') {
                    anySuccess = true;
                    closed.push(...r.value);
                  } else {
                    errors.push(String(r.reason?.message || r.reason));
                  }
                }
                if (!anySuccess) {
                  throw new Error(errors.join('; ') || 'Không thể đọc lịch sử đóng ticket FTMS');
                }
                return closed;
              };

              let tickets = [];
              let closedTickets = [];
              try {
                const tasks = [];
                if ({{activeFlag}}) tasks.push(fetchActive());
                if ({{historyFlag}}) tasks.push(fetchHistory());
                const results = await Promise.allSettled(tasks);
                let resultIdx = 0;
                if ({{activeFlag}}) {
                  const activeResult = results[resultIdx++];
                  if (activeResult.status !== 'fulfilled') {
                    const message = String(activeResult.reason?.message || activeResult.reason || '');
                    return JSON.stringify({ error: message.includes('401') ? 'FTMS API HTTP 401' : (message || 'Lỗi đọc danh sách ticket FTMS') });
                  }
                  tickets = activeResult.value;
                }
                if ({{historyFlag}}) {
                  const historyResult = results[resultIdx++];
                  if (historyResult.status === 'fulfilled') {
                    closedTickets = historyResult.value;
                  } else if (!{{activeFlag}}) {
                    const message = String(historyResult.reason?.message || historyResult.reason || '');
                    return JSON.stringify({ error: message.includes('401') ? 'FTMS history HTTP 401' : (message || 'Lỗi đọc lịch sử đóng ticket FTMS') });
                  }
                }
              } catch (error) {
                return JSON.stringify({ error: String(error) });
              }

              if (!{{historyFlag}}) {
                return JSON.stringify({ data: tickets, count: tickets.length });
              }
              if (!{{activeFlag}}) {
                return JSON.stringify({ data: closedTickets, count: closedTickets.length });
              }

              // Prefer the newest authoritative representation. A newer active row means the ticket
              // was reopened; otherwise a validated close-history row must not be hidden by stale active data.
              const byCode = new Map();
              for (const ticket of tickets) {
                const key = ticket.code.toLocaleUpperCase('vi-VN');
                const current = byCode.get(key);
                if (!current || (!current.updatedAt && ticket.updatedAt) ||
                    (ticket.updatedAt && current.updatedAt && new Date(ticket.updatedAt) > new Date(current.updatedAt)))
                  byCode.set(key, ticket);
              }
              for (const ticket of closedTickets) {
                const key = ticket.code.toLocaleUpperCase('vi-VN');
                const current = byCode.get(key);
                const closedTime = new Date(ticket.closedAt).getTime();
                const currentTime = current?.updatedAt ? new Date(current.updatedAt).getTime() : Number.NEGATIVE_INFINITY;
                if (!current || current.status === 5 || closedTime >= currentTime) byCode.set(key, ticket);
              }
              const allTickets = Array.from(byCode.values());
              return JSON.stringify({ data: allTickets, count: allTickets.length });
            })()
            """;
    }

    public async Task<TicketClaimResult> ClaimTicketAsync(string ticketCode, long expectedUserId, CancellationToken cancellationToken)
    {
        var normalizedCode = ticketCode?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedCode))
            return new TicketClaimResult(TicketClaimStatus.NotFound, "Mã ticket không hợp lệ.");

        var identity = await GetCurrentUserAsync(cancellationToken);
        if (identity?.UserId != expectedUserId)
            return new TicketClaimResult(TicketClaimStatus.AuthenticationRequired,
                "Tài khoản FTMS đang đăng nhập không khớp với tài khoản giám sát.");
        if (string.IsNullOrWhiteSpace(identity.UserName))
            return new TicketClaimResult(TicketClaimStatus.AuthenticationRequired,
                "FTMS chưa cung cấp tên tài khoản để nhận ticket.");

        var code = JsonSerializer.Serialize(normalizedCode);
        var script = $$"""
            (async () => {
              const code = {{code}};
              const expectedUserId = {{expectedUserId}};
              const isCase = /^(CA|AL)/i.test(code);
              const listEndpoints = isCase
                ? ['/ihub/case/GetListCasesV12', '/ihub/case/GetListCasesRequest']
                : ['/ihub/request/GetListRequestV12'];
              const claimEndpoint = isCase ? '/ihub/Case/TakeAndAssignmentV12' : '/ihub/Request/TakeAndAssignmentV12';
              const unwrap = (value, depth = 0) => {
                if (depth > 8 || value == null) return [];
                if (Array.isArray(value)) return value;
                if (typeof value === 'string') {
                  try { return unwrap(JSON.parse(value), depth + 1); } catch { return []; }
                }
                if (typeof value !== 'object') return [];
                for (const key of ['data','Data','rows','Rows','items','Items','result','Result']) {
                  const rows = unwrap(value[key], depth + 1);
                  if (rows.length) return rows;
                }
                for (const child of Object.values(value)) {
                  const rows = unwrap(child, depth + 1);
                  if (rows.some(x => x && typeof x === 'object')) return rows;
                }
                return [];
              };
              const pick = (row, ...keys) => {
                for (const key of keys) if (row?.[key] !== undefined && row[key] !== null && row[key] !== '') return row[key];
                return null;
              };
              const numberOf = value => {
                const parsed = Number(value);
                return value === null || value === undefined || value === '' || !Number.isFinite(parsed) ? null : parsed;
              };
              const ticketCodeOf = row => String(pick(row, 'code','Code','requestCode','RequestCode','caseCode','CaseCode') || '').trim();
              const assigneeOf = row => numberOf(pick(row, 'staffId','StaffId','agentId','AgentId','assigneeId','AssigneeId','ASSIGNEE_ID'));
              const claimIdOf = row => pick(row, 'assignId','assignID','assignmentId','AssignmentId','assignmentID',
                'requestAssignId','RequestAssignId','requestAssignID','strID','intID','requestID','RequestID','requestId',
                'id','Id','ID','ticketId','TicketId');
              const statusOf = row => {
                const numeric = numberOf(pick(row, 'status','Status','statusId','StatusId','STATUS_ID'));
                if (numeric !== null) return numeric;
                const name = String(pick(row, 'statusName','StatusName','statusText','StatusText') || '')
                  .trim().toLocaleLowerCase('vi-VN');
                return { 'mới': 0, 'tạo mới': 0, 'new': 0, 'phân công': 1, 'assigned': 1,
                  'đang thực hiện': 2, 'đang xử lý': 2, 'in progress': 2,
                  'hoàn thành': 3, 'completed': 3, 'tạm ngưng': 4, 'paused': 4,
                  'đóng': 5, 'đã đóng': 5, 'closed': 5, 'hủy': 7, 'đã hủy': 7,
                  'cancelled': 7, 'không xử lý': 8 }[name] ?? null;
              };
              const findTicket = async () => {
                const body = new URLSearchParams({ take: '50', skip: '0', page: '1', pageSize: '50',
                  search: code, isMyTicket: '', isAkabot: '', strStatus: '', strRegionID: '', linkDeptId: '',
                  alarmType: '0', isSortByDate: '' });
                let hadSuccessfulResponse = false;
                let lastError = '';
                for (const endpoint of listEndpoints) {
                  try {
                    const response = await fetch(endpoint, {
                      method: 'POST', credentials: 'same-origin',
                      headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
                        'X-Requested-With': 'XMLHttpRequest' }, body: body.toString()
                    });
                    if (/\/id\/login|\/adfs\//i.test(new URL(response.url).pathname))
                      return { authenticationRequired: true };
                    if (!response.ok) {
                      lastError = `${endpoint} HTTP ${response.status}`;
                      continue;
                    }
                    const rows = unwrap(await response.json());
                    hadSuccessfulResponse = true;
                    const row = rows.find(x =>
                      ticketCodeOf(x).toLocaleUpperCase('vi-VN') === code.toLocaleUpperCase('vi-VN'));
                    if (row) return { row };
                  } catch (error) { lastError = String(error); }
                }
                return hadSuccessfulResponse ? {} : { retryableFailure: true, error: lastError };
              };
              try {
                if (Number(globalThis.userID) !== expectedUserId || typeof globalThis.Username === 'undefined')
                  return JSON.stringify({ status: 'AuthenticationRequired', message: 'Phiên FTMS không đúng tài khoản.' });

                let ticketId = '';
                let currentOwner = null;
                let currentStatus = null;

                const cached = globalThis.__ftmsTicketCache?.get(code.toLocaleUpperCase('vi-VN'));
                if (cached && cached.id) {
                  ticketId = cached.id;
                  currentOwner = cached.owner;
                  currentStatus = cached.status;
                }

                if (!ticketId) {
                  const grid = globalThis.jQuery?.('#list-grid').data('kendoGrid');
                  const gridRow = grid?.dataSource?.data()?.find(x =>
                    ticketCodeOf(x).toLocaleUpperCase('vi-VN') === code.toLocaleUpperCase('vi-VN'));
                  if (gridRow) {
                    ticketId = String(claimIdOf(gridRow) || '').trim();
                    currentOwner = assigneeOf(gridRow);
                    currentStatus = statusOf(gridRow);
                  }
                }

                if (!ticketId) {
                  let before = await findTicket();
                  if (before.authenticationRequired)
                    return JSON.stringify({ status: 'AuthenticationRequired', message: 'Phiên đăng nhập FTMS đã hết hạn.' });
                  if (before.retryableFailure)
                    return JSON.stringify({ status: 'RetryableFailure', message: before.error || 'Không đọc được danh sách FTMS.' });
                  if (before.row) {
                    ticketId = String(claimIdOf(before.row) || '').trim();
                    currentOwner = assigneeOf(before.row);
                    currentStatus = statusOf(before.row);
                  }
                }

                if (!ticketId)
                  return JSON.stringify({ status: 'NotFound', message: `Không tìm thấy ${code} trên FTMS.` });

                const alreadyOwnedInProgress = currentOwner === expectedUserId && currentStatus === 2;
                if (currentOwner === expectedUserId && !alreadyOwnedInProgress)
                  return JSON.stringify({ status: 'AlreadyOwnedByCurrentUser', message: `${code} đã được nhận bởi tài khoản hiện tại.` });
                if (currentOwner && currentOwner !== expectedUserId)
                  return JSON.stringify({ status: 'OwnedByAnotherUser', message: `${code} đã được người khác nhận.` });
                if ([5, 7, 8].includes(currentStatus))
                  return JSON.stringify({ status: 'NotClaimable', message: `${code} đã kết thúc, không thể nhận.` });

                if (!/^\d{1,20}$/.test(ticketId))
                  return JSON.stringify({ status: 'NotFound', message: `Không đọc được ID nhận hợp lệ của ${code}.` });

                const readAntiForgeryToken = root => String(
                  root?.querySelector('input[name="__RequestVerificationToken"], input[name="RequestVerificationToken"]')?.value ||
                  root?.querySelector('meta[name="__RequestVerificationToken"], meta[name="request-verification-token"], meta[name="csrf-token"]')?.getAttribute('content') || '').trim();
                let antiForgeryToken = readAntiForgeryToken(document) || globalThis.__ftmsCachedToken || '';
                if (!antiForgeryToken) {
                  try {
                    const homeResponse = await fetch('/ihub/', { credentials: 'same-origin', cache: 'no-store' });
                    if (/\/id\/login|\/adfs\//i.test(new URL(homeResponse.url).pathname))
                      return JSON.stringify({ status: 'AuthenticationRequired', message: 'Phiên đăng nhập FTMS đã hết hạn.' });
                    if (homeResponse.ok) {
                      const homeHtml = await homeResponse.text();
                      const homeDocument = new DOMParser().parseFromString(homeHtml, 'text/html');
                      antiForgeryToken = readAntiForgeryToken(homeDocument);
                    }
                  } catch { }
                }
                if (antiForgeryToken) globalThis.__ftmsCachedToken = antiForgeryToken;

                const body = new URLSearchParams();
                body.append('input[id_Ticket]', ticketId);
                body.append('input[creator]', String(globalThis.Username));
                body.append('input[staff_ID]', String(globalThis.userID));
                body.append('input[staffName]', String(globalThis.Username));
                body.append('input[departmentName]', String(globalThis.DepartmentName || ''));
                body.append('input[department_ID]', String(globalThis.UserDept || ''));
                body.append('input[createDate]', String(Date.now()));
                body.append('input[type]', '2');
                body.append('input[code]', code);
                body.append('input[ticketStatus]', '2');
                body.append('type', '2');
                if (antiForgeryToken) body.append('__RequestVerificationToken', antiForgeryToken);

                const headers = {
                  'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
                  'X-Requested-With': 'XMLHttpRequest'
                };
                if (antiForgeryToken) {
                  headers.Antiforgerykeyheader = antiForgeryToken;
                  headers.RequestVerificationToken = antiForgeryToken;
                  headers.__RequestVerificationToken = antiForgeryToken;
                }

                let serverAccepted = alreadyOwnedInProgress;
                let serverMessage = '';
                if (!alreadyOwnedInProgress) {
                  try {
                    const response = await fetch(claimEndpoint, {
                      method: 'POST', credentials: 'same-origin', cache: 'no-store',
                      headers, body: body.toString()
                    });
                    if (/\/id\/login|\/adfs\//i.test(new URL(response.url).pathname))
                      return JSON.stringify({ status: 'AuthenticationRequired', message: 'Phiên đăng nhập FTMS đã hết hạn.' });
                    const text = await response.text();
                    let parsed = null;
                    if (text) {
                      try { parsed = JSON.parse(text); }
                      catch { serverMessage = text.slice(0, 200); }
                    }
                    serverMessage = String(parsed?.message?.value ?? parsed?.message ?? parsed?.Message ??
                      parsed?.error ?? parsed?.Error ?? serverMessage);
                    serverAccepted = response.ok && Boolean(parsed &&
                      (parsed.result === true || parsed.result === 1 ||
                        /^(true|1)$/i.test(String(parsed.result ?? '')) ||
                        String(parsed.status ?? '').toLowerCase() === 'ok'));
                    if (!response.ok && !serverMessage) serverMessage = `FTMS HTTP ${response.status}`;
                  } catch (error) { serverMessage = String(error); }

                  if (!serverAccepted)
                    return JSON.stringify({ status: 'RetryableFailure',
                      message: serverMessage || `FTMS từ chối lệnh nhận ${code}.` });
                }

                if (!isCase) {
                  const changeStatusBody = new URLSearchParams({
                    deptId: String(globalThis.UserDept || ''),
                    staffId: String(globalThis.userID),
                    departmentName: String(globalThis.DepartmentName || ''),
                    staffName: String(globalThis.Username),
                    department: String(globalThis.Department || globalThis.DepartmentCode ||
                      globalThis.DeptCode || globalThis.UserDepartmentCode || ''),
                    status: '4',
                    reasonId: '',
                    statusChangeId: '2',
                    statusChangeNote: 'Hỗ trợ KH',
                    id: ticketId,
                    oldStatus: '2',
                    code,
                    type: '10'
                  });
                  let statusMessage = '';
                  try {
                    const statusResponse = await fetch('/ihub/Request/ChangeStatus', {
                      method: 'POST', credentials: 'same-origin', cache: 'no-store',
                      headers, body: changeStatusBody.toString()
                    });
                    if (/\/id\/login|\/adfs\//i.test(new URL(statusResponse.url).pathname))
                      return JSON.stringify({ status: 'RetryableFailure',
                        message: `Đã nhận ${code}, nhưng phiên FTMS hết hạn khi chuyển sang Pending Customer.` });
                    const statusText = await statusResponse.text();
                    let statusResult = null;
                    if (statusText) {
                      try { statusResult = JSON.parse(statusText); }
                      catch { statusMessage = statusText.slice(0, 200); }
                    }
                    statusMessage = String(statusResult?.message?.value ?? statusResult?.message ?? statusResult?.Message ??
                      statusResult?.error ?? statusResult?.Error ?? statusMessage);
                    const statusAccepted = statusResponse.ok && Boolean(statusResult &&
                      (Number(statusResult.code) === 200 || Number(statusResult.status) === 200 ||
                        statusResult.result === true || statusResult.result === 1 ||
                        /^(true|1)$/i.test(String(statusResult.result ?? '')) ||
                        statusResult.success === true || String(statusResult.status ?? '').toLowerCase() === 'ok' ||
                        /cập nhật thành công/i.test(statusMessage)));
                    if (!statusAccepted)
                      return JSON.stringify({ status: 'RetryableFailure',
                        message: statusMessage || `Đã nhận ${code}, nhưng FTMS từ chối chuyển sang Pending Customer.` });
                  } catch (error) {
                    return JSON.stringify({ status: 'RetryableFailure',
                      message: `Đã nhận ${code}, nhưng chưa chuyển được sang Pending Customer: ${String(error)}` });
                  }
                  return JSON.stringify({ status: 'Claimed',
                    message: statusMessage || `Đã nhận ${code} và chuyển sang Tạm ngưng / Pending Customer (Hỗ trợ KH).` });
                }

                return JSON.stringify({ status: 'Claimed',
                  message: serverMessage || `Đã nhận ${code} trên FTMS.` });
              } catch (error) {
                return JSON.stringify({ status: 'RetryableFailure', message: String(error) });
              }
            })()
            """;
        var json = await ExecuteAsyncJsonStringAsync(script, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                using var nested = JsonDocument.Parse(root.GetString() ?? "{}");
                root = nested.RootElement.Clone();
            }
            var statusText = root.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
            var message = root.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
            if (Enum.TryParse<TicketClaimStatus>(statusText, out var status))
                return new TicketClaimResult(status, message ?? "FTMS không trả về mô tả.");
        }
        catch (JsonException) { }
        return new TicketClaimResult(TicketClaimStatus.RetryableFailure, "Không đọc được kết quả nhận ticket từ FTMS.");
    }

    private async Task<IReadOnlyList<TicketSnapshot>> GetTicketsLegacyAsync(CancellationToken cancellationToken)
    {
        const string script = """
            (() => {
              const visibleCards = Array.from(document.querySelectorAll('#list-grid .card-list'));
              if (visibleCards.length) {
                const statusMap = { 'tạo mới': 0, 'mới': 0, 'phân công': 1, 'đang thực hiện': 2,
                  'hoàn thành': 3, 'tạm ngưng': 4, 'đóng': 5, 'đã đóng': 5, 'hủy': 7, 'không xử lý': 8 };
                const text = (root, selector) => root.querySelector(selector)?.textContent?.trim() || null;
                return JSON.stringify(visibleCards.map(card => {
                  const statusText = (text(card, '.ticket-status') || '').toLocaleLowerCase('vi-VN');
                  const createdText = text(card, '.ticket-create-time');
                  let createdAt = null;
                  const match = createdText?.match(/(\d{2}):(\d{2}):(\d{2})\s*-\s*(\d{2})\/(\d{2})\/(\d{4})/);
                  if (match) createdAt = `${match[6]}-${match[5]}-${match[4]}T${match[1]}:${match[2]}:${match[3]}+07:00`;
                  return { code: text(card, '.ticket-code'), status: statusMap[statusText] ?? 0,
                    title: text(card, '.ticket-title'), createdAt, assigneeName: text(card, '.ticket-handler'),
                    departmentName: text(card, '.ticket-department'), slaDeviationMinutes: null, slaType: null };
                }).filter(x => x.code));
              }
              let result = null;
              try {
                const body = new URLSearchParams({ take: '500', skip: '0', page: '1', pageSize: '500',
                  search: '', isMyTicket: '', isAkabot: '', strStatus: '', strRegionID: '', linkDeptId: '',
                  alarmType: '0', isSortByDate: '' });
                const xhr = new XMLHttpRequest(); xhr.open('POST', '/ihub/request/GetListRequestV12', false);
                xhr.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded; charset=UTF-8');
                xhr.send(body.toString());
                if (xhr.status >= 200 && xhr.status < 300) result = JSON.parse(xhr.responseText);
              } catch {}
              const findRows = (value, depth = 0) => {
                if (depth > 6 || value == null) return [];
                if (Array.isArray(value)) return value;
                if (typeof value === 'string') {
                  try { return findRows(JSON.parse(value), depth + 1); } catch { return []; }
                }
                if (typeof value !== 'object') return [];
                for (const key of ['data','Data','rows','Rows','items','Items','result','Result']) {
                  const rows = findRows(value[key], depth + 1);
                  if (rows.length) return rows;
                }
                for (const child of Object.values(value)) {
                  const rows = findRows(child, depth + 1);
                  if (rows.length && rows.some(x => x && typeof x === 'object' && ('code' in x || 'Code' in x))) return rows;
                }
                return [];
              };
              let rows = findRows(result);
              const cards = Array.from(document.querySelectorAll('#list-grid .card-list'));
              const hasTicketCode = rows.some(x => x && typeof x === 'object' &&
                (x.code || x.Code || x.requestCode || x.RequestCode || x.REQUEST_CODE || x.REQUESTCODE));
              if (cards.length || !hasTicketCode) {
                const statusMap = { 'tạo mới': 0, 'mới': 0, 'phân công': 1, 'đang thực hiện': 2,
                  'hoàn thành': 3, 'tạm ngưng': 4, 'đóng': 5, 'đã đóng': 5, 'hủy': 7, 'không xử lý': 8 };
                const text = (root, selector) => root.querySelector(selector)?.textContent?.trim() || null;
                rows = cards.map(card => {
                  const statusText = (text(card, '.ticket-status') || '').toLocaleLowerCase('vi-VN');
                  const createdText = text(card, '.ticket-create-time');
                  let createdAt = null;
                  const match = createdText?.match(/(\d{2}):(\d{2}):(\d{2})\s*-\s*(\d{2})\/(\d{2})\/(\d{4})/);
                  if (match) createdAt = `${match[6]}-${match[5]}-${match[4]}T${match[1]}:${match[2]}:${match[3]}+07:00`;
                  return { code: text(card, '.ticket-code'), status: statusMap[statusText] ?? 0,
                    title: text(card, '.ticket-title'), createdAt, assigneeName: text(card, '.ticket-handler'),
                    departmentName: text(card, '.ticket-department'), slaDeviationMinutes: null, slaType: null };
                }).filter(x => x.code);
              }
              return JSON.stringify(rows.map(x => ({ code: x.code || x.Code || x.requestCode || x.RequestCode || x.REQUEST_CODE || x.REQUESTCODE,
                status: Number(x.status ?? x.Status ?? x.statusId ?? x.StatusId ?? x.STATUS_ID ?? 0),
                title: x.title || x.Title, createdAt: x.createDate || x.CreateDate, assigneeId: x.staffId || x.StaffId,
                assigneeName: x.agentName || x.AgentName || x.staffName || x.StaffName,
                departmentId: x.deptId || x.DeptId, departmentName: x.department || x.Department,
                slaDeviationMinutes: x.slaDeviation ?? x.SlaDeviation, slaType: x.typeSLA ?? x.TypeSLA })));
            })()
            """;
        var json = await ExecuteJsonStringAsync(script);
        return DeserializeTickets(json);
    }

    public async Task<LatestEmail?> GetLatestEmailAsync(string ticketCode, CancellationToken cancellationToken)
    {
        var code = JsonSerializer.Serialize(ticketCode);
        var script = $$"""
            (async () => {
              try {
                const parseMailDate = value => {
                  if (!value) return null;
                  const normalized = String(value).replace(/(\d{1,2})(?:st|nd|rd|th)\b/gi, '$1').trim();
                  const dotNet = normalized.match(/^\/Date\((\d+)(?:[+-]\d+)?\)\/$/);
                  if (dotNet) return new Date(Number(dotNet[1]));
                  const local = normalized.match(/^(\d{1,2}):(\d{2})(?::(\d{2}))?\s*-?\s*(\d{1,2})\/(\d{1,2})\/(\d{4})$/) ||
                    normalized.match(/^(\d{1,2})\/(\d{1,2})\/(\d{4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?$/);
                  if (local) {
                    const timeFirst = /^\d{1,2}:\d{2}/.test(normalized);
                    const [day, month, year, hour, minute, second] = timeFirst
                      ? [local[4], local[5], local[6], local[1], local[2], local[3] || '00']
                      : [local[1], local[2], local[3], local[4], local[5], local[6] || '00'];
                    const iso = `${year}-${month.padStart(2, '0')}-${day.padStart(2, '0')}T${hour.padStart(2, '0')}:${minute}:${second}+07:00`;
                    const parsed = new Date(iso);
                    return Number.isNaN(parsed.getTime()) ? null : parsed;
                  }
                  const isoWithoutZone = normalized.match(/^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?$/);
                  const parsed = new Date(isoWithoutZone ? normalized.replace(' ', 'T') + '+07:00' : normalized);
                  return Number.isNaN(parsed.getTime()) ? null : parsed;
                };
                const emailAddress = value => String(value || '').match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/i)?.[0] || null;
                const ignoredSender = (...values) => ['fti.sd02@fpt.com','ihub.akabot2','ducvm19@fpt.com']
                  .some(ignored => values.some(value => String(value || '').toLowerCase().includes(ignored)));
                const automatedReceipt = body => {
                  const latest = String(body || '').split(/<hr\b|-----Original Message-----|From:\s*.{0,200}?\b(?:Sent|Date):/i)[0];
                  const text = latest.replace(/<[^>]+>/g, ' ').normalize('NFD')
                    .replace(/[\u0300-\u036f]/g, '').toLowerCase();
                  return (text.includes('thong tin yeu cau ho tro') && text.includes('da duoc tiep nhan')) ||
                    (text.includes('thong tin ho tro') && text.includes('ky thuat se kiem tra va phan hoi')) ||
                    text.includes('itsm - phong ho tro khach hang') || text.includes('tong dai 1900 6973');
                };
                const unwrapRows = (value, depth = 0) => {
                  if (depth > 6 || value == null) return [];
                  if (Array.isArray(value)) return value;
                  if (typeof value === 'string') {
                    try { return unwrapRows(JSON.parse(value), depth + 1); } catch { return []; }
                  }
                  if (typeof value !== 'object') return [];
                  for (const key of ['Data','data','Rows','rows','Items','items','Result','result','list','List']) {
                    const rows = unwrapRows(value[key], depth + 1);
                    if (rows.length) return rows;
                  }
                  return [];
                };
                const isTruncated = text => {
                  const s = String(text || '').trim();
                  if (!s || /\.{3}$/.test(s) || /…$/.test(s) || /&nb\.{3}$/i.test(s) || /&nb…$/i.test(s)) return true;
                  const compact = s.replace(/\s+/g, ' ').replace(/[.!,:;]+$/g, '').trim();
                  return compact.length <= 80 && /^(?:dear|hi|hello|chao|chào|xin chao|xin chào)\s+\S+(?:\s+\S+){0,5}$/i.test(compact);
                };
                const readMailFile = async fileId => {
                  if (!fileId) return '';
                  const cleanId = String(fileId).trim();
                  try {
                    let response = await fetch('/ihub/email/ReadMailFromFile?fileId=' + encodeURIComponent(cleanId), {
                      credentials: 'same-origin',
                      headers: { 'X-Requested-With': 'XMLHttpRequest' }
                    });
                    if (!response.ok) {
                      response = await fetch('/ihub/email/ReadMailFromFile?fileId=' + encodeURIComponent(cleanId) + '&storage=2', {
                        credentials: 'same-origin',
                        headers: { 'X-Requested-With': 'XMLHttpRequest' }
                      });
                    }
                    if (!response.ok) return '';
                    let text = await response.text();
                    if (!text) return '';
                    try {
                      const parsed = JSON.parse(text);
                      if (typeof parsed === 'string') text = parsed;
                      else if (parsed && typeof parsed === 'object') {
                        text = parsed.contents || parsed.Contents || parsed.content || parsed.Content ||
                               parsed.body || parsed.Body || parsed.html || parsed.Html ||
                               (typeof parsed.data === 'string' ? parsed.data : parsed.data?.content || parsed.data?.body || parsed.data?.contents) ||
                               (typeof parsed.Data === 'string' ? parsed.Data : parsed.Data?.Content || parsed.Data?.Body || parsed.Data?.Contents) ||
                               text;
                      }
                    } catch {}
                    const str = String(text || '').trim();
                    if (str && str !== '[object Object]') return str;
                  } catch {}
                  return '';
                };
                const extractFileId = obj => {
                  if (!obj) return null;
                  const normalized = Object.fromEntries(Object.entries(obj || {}).map(([k, v]) => [k.replace(/[^a-z0-9]/gi, '').toLowerCase(), v]));
                  const keys = ['fileid', 'file_id', 'emailfileid', 'email_file_id', 'attachfileid', 'attach_file_id',
                    'strfileid', 'sourcefileid', 'mailfileguid', 'emailfileguid', 'fileguid', 'fileuuid', 'uuid', 'guid', 'file'];
                  for (const key of keys) {
                    const val = normalized[key.replace(/[^a-z0-9]/gi, '').toLowerCase()];
                    if (val !== undefined && val !== null && String(val).trim()) return String(val).trim();
                  }
                  if (obj.contentFile && typeof obj.contentFile === 'object') {
                    const nested = extractFileId(obj.contentFile);
                    if (nested) return nested;
                  }
                  if (typeof obj.recipients === 'string') {
                    try {
                      const parsed = JSON.parse(obj.recipients);
                      const nested = extractFileId(parsed);
                      if (nested) return nested;
                    } catch {}
                  }
                  for (const [k, v] of Object.entries(obj)) {
                    if (typeof v === 'string') {
                      const m = v.match(/[?&]fileId=([^&#\s]+)/i);
                      if (m) return decodeURIComponent(m[1]).trim();
                    }
                  }
                  return null;
                };

                // FTMS expects jQuery's default form encoding rather than JSON.
                const historyResponse = await fetch('/ihub/Email/GetEmailByCode', {
                  method: 'POST',
                  credentials: 'same-origin',
                  headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
                    'X-Requested-With': 'XMLHttpRequest' },
                  body: new URLSearchParams({ code: {{code}} }).toString()
                });
                if (historyResponse.ok) {
                  let history = unwrapRows(await historyResponse.json());
                  if (history.length) {
                    const dateValue = value => {
                      const fallback = Object.entries(value || {}).find(([key, fieldValue]) =>
                        /(?:date|time|sent|send|created)/i.test(key) && fieldValue)?.[1];
                      const raw = value?.sendDate || value?.SendDate || value?.sentDate || value?.SentDate ||
                        value?.emailDate || value?.EmailDate ||
                        value?.createDate || value?.CreateDate || value?.createdDate || value?.CreatedDate ||
                        value?.date || value?.Date || fallback;
                      const parsed = parseMailDate(raw); return parsed?.getTime() || 0;
                    };
                    history.sort((a, b) => dateValue(b) - dateValue(a) || Number(b.id || b.Id || 0) - Number(a.id || a.Id || 0));
                    for (const mail of history) {
                      const normalizedMail = Object.fromEntries(Object.entries(mail || {}).map(([key, value]) => [key.replace(/[^a-z0-9]/gi, '').toLowerCase(), value]));
                      const mailField = (...names) => {
                        for (const name of names) {
                          const value = normalizedMail[name.replace(/[^a-z0-9]/gi, '').toLowerCase()];
                          if (value !== undefined && value !== null && value !== '') return value;
                        }
                        return null;
                      };
                      const emailId = mailField('id', 'emailhistoryid', 'maxeh');
                      const fileId = extractFileId(mail);
                      let body = '';
                      if (fileId) body = await readMailFile(fileId);
                      if (!body) body = mailField('contents', 'content', 'body') || '';
                      if (String(body).trim() === String(mailField('subject', 'emailsubject', 'title') || '').trim()) body = '';
                      let sender = [mailField('mailposter', 'posteremail', 'poster', 'senderemail', 'sender',
                        'emailfrom', 'fromemail', 'fromaddress', 'mailfrom', 'from')].map(emailAddress).find(Boolean);
                      if (!sender) {
                        const candidates = Object.entries(mail).flatMap(([key, value]) => {
                          if (!/(?:from|sender|poster)/i.test(key)) return [];
                          const emails = String(value || '').match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/ig) || [];
                          return emails.map(email => ({ key, email }));
                        });
                        sender = candidates[0]?.email || null;
                      }
                      if (ignoredSender(sender, mailField('poster', 'mailposter', 'sender', 'from')) ||
                          automatedReceipt(body)) continue;
                      const fallbackDate = Object.entries(mail).find(([key, fieldValue]) =>
                        /(?:date|time|sent|send|created)/i.test(key) && fieldValue)?.[1];
                      const rawDate = mailField('senddate', 'sentdate', 'emaildate', 'createdate', 'createddate',
                        'date', 'lasttimeresponse', 'lastresponsetime', 'timeresponse', 'responsetime', 'sentat') || fallbackDate;
                      const parsedDate = parseMailDate(rawDate);
                      return JSON.stringify({ id: String(emailId || fileId || ''),
                        sentAt: parsedDate?.toISOString() || null,
                        from: sender, subject: mailField('subject', 'emailsubject', 'title') || null, body });
                    }
                    // All email rows were deliberately excluded; the C88 fallback
                    // must not reintroduce an automated sender.
                    return JSON.stringify(null);
                  }
                }
                const form = new FormData(); form.append('id', 'C88'); form.append('body', JSON.stringify({ p_strCode: {{code}} }));
                const metaResponse = await fetch('/ihub/code/code', {
                  method: 'POST',
                  credentials: 'same-origin',
                  headers: { 'X-Requested-With': 'XMLHttpRequest' },
                  body: form
                });
                if (!metaResponse.ok) return JSON.stringify(null);
                const meta = await metaResponse.json();
                const rows = unwrapRows(meta);
                if (!rows.length) return JSON.stringify(null);
                const historyValue = row => {
                  const normalized = Object.fromEntries(Object.entries(row).map(([key, value]) => [key.replace(/[^a-z0-9]/gi, '').toLowerCase(), value]));
                  const rawDate = normalized.lasttimeresponse || normalized.lastresponsetime || normalized.timeresponse ||
                    normalized.responsetime || normalized.senddate || normalized.sentat || normalized.createdate;
                  const parsed = parseMailDate(rawDate)?.getTime() || 0;
                  const rawId = normalized.maxeh || normalized.emailhistoryid || normalized.id || 0;
                  return { time: Number.isNaN(parsed) ? 0 : parsed, id: Number(rawId) || 0 };
                };
                const sortedRows = [...rows].sort((a, b) => {
                  const left = historyValue(a), right = historyValue(b);
                  return right.time - left.time || right.id - left.id;
                });
                for (const row of sortedRows) {
                  const normalized = Object.fromEntries(Object.entries(row).map(([key, value]) => [key.replace(/[^a-z0-9]/gi, '').toLowerCase(), value]));
                  const field = (...names) => {
                    for (const name of names) {
                      const value = normalized[name.replace(/[^a-z0-9]/gi, '').toLowerCase()];
                      if (value !== undefined && value !== null && value !== '') return value;
                    }
                    return null;
                  };
                  const emailId = field('MAX_EH', 'EMAIL_HISTORY_ID', 'ID');
                  const fileId = extractFileId(row);
                  let body = '';
                  if (fileId) body = await readMailFile(fileId);
                  if (!body) body = field('CONTENTS', 'CONTENT', 'BODY') || '';
                  const rawSentAt = field('LAST_TIME_RESPONSE', 'LAST_RESPONSE_TIME', 'TIME_RESPONSE', 'RESPONSE_TIME', 'SEND_DATE', 'SENT_AT', 'CREATE_DATE');
                  const parsedSentAt = parseMailDate(rawSentAt);
                  let sentAt = parsedSentAt?.toISOString() || null;
                  let sender = ['MAIL_POSTER', 'POSTER_EMAIL', 'POSTER', 'SENDER_EMAIL', 'SENDER',
                    'EMAIL_FROM', 'FROM_EMAIL', 'EMAILFROM', 'FROM_ADDRESS', 'FROMADDRESS', 'FROM']
                    .map(name => emailAddress(field(name))).find(Boolean);
                  if (!sender) {
                    const candidates = Object.entries(row).flatMap(([key, value]) => {
                      if (!/(?:from|sender|poster)/i.test(key)) return [];
                      const emails = String(value || '').match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/ig) || [];
                      return emails.map(email => ({ key, email }));
                    });
                    sender = candidates[0]?.email || null;
                  }
                  if (ignoredSender(sender, field('MAIL_POSTER','POSTER','SENDER','FROM')) || automatedReceipt(body)) continue;
                  return JSON.stringify({ id: String(emailId || fileId || ''), sentAt,
                    from: sender, subject: field('SUBJECT', 'EMAIL_SUBJECT', 'TITLE') || null, body });
                }
                return JSON.stringify(null);
              } catch { return JSON.stringify(null); }
            })()
            """;
        var json = await ExecuteAsyncJsonStringAsync(script, cancellationToken);
        return DeserializeLatestEmail(json);
    }

    public async Task<StatusHistoryEntry?> GetLatestStatusHistoryAsync(string ticketCode, TicketStatus status, CancellationToken cancellationToken)
    {
        var code = JsonSerializer.Serialize(ticketCode);
        var route = ticketCode.StartsWith("CA", StringComparison.OrdinalIgnoreCase) || ticketCode.StartsWith("AL", StringComparison.OrdinalIgnoreCase)
            ? "case" : "request";
        var expectedStatus = (int)status;
        var script = $$"""
            (async () => {
              try {
                // The ticket page loads its timeline asynchronously from this API.
                // The HTML returned by /edit/{code} does not contain the rendered entries.
                const timelineResponse = await fetch('/ihub/{{route}}/GetTimeLineByCode?code=' + encodeURIComponent({{code}}), {
                  credentials: 'same-origin',
                  headers: { 'X-Requested-With': 'XMLHttpRequest' }
                });
                if (timelineResponse.ok) {
                  const unwrap = (value, depth = 0) => {
                    if (depth > 6 || value == null) return [];
                    if (Array.isArray(value)) return value;
                    if (typeof value === 'string') {
                      try { return unwrap(JSON.parse(value), depth + 1); } catch { return []; }
                    }
                    if (typeof value !== 'object') return [];
                    for (const key of ['data','Data','result','Result','rows','Rows']) {
                      const rows = unwrap(value[key], depth + 1);
                      if (rows.length) return rows;
                    }
                    return [];
                  };
                  const parseDate = value => {
                    if (!value) return null;
                    const raw = String(value).trim();
                    const dotNet = raw.match(/^\/Date\((\d+)(?:[+-]\d+)?\)\/$/);
                    if (dotNet) return new Date(Number(dotNet[1])).toISOString();
                    const local = /^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?$/.test(raw);
                    const parsed = new Date(local ? raw.replace(' ', 'T') + '+07:00' : raw);
                    return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
                  };
                  const rows = unwrap(await timelineResponse.json())
                    .map(row => ({ status: Number(row.status ?? row.Status ?? row.statusId ?? row.StatusId),
                      occurredAt: parseDate(row.createDate ?? row.CreateDate ?? row.date ?? row.Date),
                      // The FTMS timeline labels "Thực hiện" with the assigned staff.
                      // "creator" can be the mail service when an email reopens a ticket.
                      actor: row.staffName ?? row.StaffName ?? row.agentName ?? row.AgentName ??
                        row.creator ?? row.Creator ?? null,
                      id: Number(row.id ?? row.Id ?? 0) }))
                    .filter(row => row.occurredAt && Number.isFinite(row.status))
                    .sort((a, b) => new Date(a.occurredAt) - new Date(b.occurredAt) || a.id - b.id);
                  const changes = rows.filter((row, index) => row.status === {{expectedStatus}} &&
                    (index === 0 || rows[index - 1].status !== row.status));
                  if (changes.length) {
                    const latest = changes[changes.length - 1];
                    return JSON.stringify({ occurredAt: latest.occurredAt, actor: latest.actor });
                  }
                }
                const editResponse = await fetch('/ihub/{{route}}/edit/' + encodeURIComponent({{code}}), {
                  credentials: 'same-origin'
                });
                if (!editResponse.ok) return JSON.stringify(null);
                const doc = new DOMParser().parseFromString(await editResponse.text(), 'text/html');
                const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
                const statusMap = { 'mới': 0, 'tạo mới': 0, 'phân công': 1, 'đang thực hiện': 2,
                  'hoàn thành': 3, 'tạm ngưng': 4, 'đóng': 5, 'đã đóng': 5, 'hủy': 7, 'đã hủy': 7,
                  'không xử lý': 8 };
                const entries = Array.from(doc.querySelectorAll('li h6')).map(heading => {
                  const item = heading.parentElement;
                  const paragraphs = Array.from(item?.querySelectorAll('p') || []).map(p => normalize(p.textContent));
                  const dateText = paragraphs.find(x => x.startsWith('Ngày:'))?.replace(/^Ngày:\s*/, '') || '';
                  const actor = paragraphs.find(x => x.startsWith('Thực hiện:'))?.replace(/^Thực hiện:\s*/, '') || null;
                  let occurredAt = null;
                  const match = dateText.match(/(\d{1,2}):(\d{2})\s*-\s*(\d{1,2})\/(\d{1,2})\/(\d{4})/);
                  if (match) occurredAt = `${match[5]}-${match[4].padStart(2,'0')}-${match[3].padStart(2,'0')}T${match[1].padStart(2,'0')}:${match[2]}:00+07:00`;
                  const statusText = normalize(heading.textContent);
                  return { status: statusMap[statusText.toLocaleLowerCase('vi-VN')], occurredAt, actor };
                }).filter(x => x.occurredAt && x.status === {{expectedStatus}});
                entries.sort((a, b) => new Date(b.occurredAt).getTime() - new Date(a.occurredAt).getTime());
                return JSON.stringify(entries[0] || null);
              } catch { return JSON.stringify(null); }
            })()
            """;
        var json = await ExecuteAsyncJsonStringAsync(script, cancellationToken);
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                using var nested = JsonDocument.Parse(root.GetString() ?? "null");
                root = nested.RootElement.Clone();
            }
            if (root.ValueKind != JsonValueKind.Object) return null;
            var actor = root.TryGetProperty("actor", out var actorValue) ? actorValue.GetString() : null;
            var occurredText = root.TryGetProperty("occurredAt", out var occurredValue) ? occurredValue.GetString() : null;
            DateTimeOffset? occurredAt = DateTimeOffset.TryParse(occurredText, out var parsed) ? parsed : null;
            return new StatusHistoryEntry(status, occurredAt, actor);
        }
        catch (JsonException) { return null; }
    }

    public Task BeginLoginRecoveryAsync(CancellationToken cancellationToken)
    {
        InvalidateCurrentUser();
        return _loginRecovery.BeginAsync(cancellationToken);
    }

    public void NotifyTargetReached()
    {
        InvalidateCurrentUser();
        _loginRecovery.NotifyTargetReached();
    }

    public bool IsTargetUri(Uri uri) => _loginRecovery.IsTargetUri(uri);

    private void InvalidateCurrentUser()
    {
        _identityGeneration++;
        _currentUser = null;
    }

    private static CurrentUserIdentity? DeserializeCurrentUser(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                using var nested = JsonDocument.Parse(root.GetString() ?? "null");
                root = nested.RootElement.Clone();
            }
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("userId", out var userIdValue)) return null;
            var userId = userIdValue.ValueKind == JsonValueKind.Number ? userIdValue.GetInt64() :
                long.TryParse(userIdValue.ToString(), out var parsedUserId) ? parsedUserId : 0;
            if (userId <= 0) return null;
            string? ReadString(string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                ? string.IsNullOrWhiteSpace(value.ToString()) ? null : value.ToString().Trim() : null;
            long? ReadLong(string name) => root.TryGetProperty(name, out var value) && long.TryParse(value.ToString(), out var parsed)
                ? parsed : null;
            return new CurrentUserIdentity(userId, ReadString("userName"), ReadLong("departmentId"), ReadString("departmentName"));
        }
        catch (JsonException) { return null; }
    }

    private const string CurrentUserScript = """
        (() => {
          try {
            const userId = Number(globalThis.userID);
            if (!Number.isInteger(userId) || userId <= 0) return JSON.stringify(null);
            const text = value => value == null || String(value).trim() === '' ? null : String(value).trim();
            const departmentId = Number(globalThis.UserDept);
            return JSON.stringify({
              userId,
              userName: text(globalThis.Username),
              departmentId: Number.isFinite(departmentId) ? departmentId : null,
              departmentName: text(globalThis.DepartmentName)
            });
          } catch { return JSON.stringify(null); }
        })()
        """;

    private async Task<string> ExecuteJsonStringAsync(string script)
    {
        var operation = await webView.Dispatcher.InvokeAsync(() => webView.ExecuteScriptAsync(script));
        var raw = await operation;
        try { return JsonSerializer.Deserialize<string>(raw) ?? "null"; }
        catch (JsonException) { return raw; }
    }

    private async Task<string> ExecuteAsyncJsonStringAsync(string script, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2WebMessageReceivedEventArgs> handler = (_, args) =>
        {
            try
            {
                using var payload = JsonDocument.Parse(args.WebMessageAsJson);
                var root = payload.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out var type) || type.GetString() != "ftms-async-result" ||
                    !root.TryGetProperty("id", out var id) || id.GetString() != requestId) return;
                if (root.TryGetProperty("error", out var error))
                    completion.TrySetException(new InvalidOperationException(error.ToString()));
                else if (root.TryGetProperty("value", out var value))
                    completion.TrySetResult(value.ValueKind == JsonValueKind.String ? value.GetString() ?? "null" : value.GetRawText());
            }
            catch (JsonException) { }
        };
        var code = $$"""
            (() => {
              Promise.resolve({{script}}).then(
                value => chrome.webview.postMessage({ type: 'ftms-async-result', id: '{{requestId}}', value }),
                error => chrome.webview.postMessage({ type: 'ftms-async-result', id: '{{requestId}}', error: String(error) })
              );
            })()
            """;
        await webView.Dispatcher.InvokeAsync(() => webView.CoreWebView2.WebMessageReceived += handler);
        try
        {
            var operation = await webView.Dispatcher.InvokeAsync(() => webView.ExecuteScriptAsync(code));
            await operation.WaitAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { return await completion.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("FTMS API không phản hồi trong 30 giây.");
            }
        }
        finally
        {
            await webView.Dispatcher.InvokeAsync(() => webView.CoreWebView2.WebMessageReceived -= handler);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static LatestEmail? DeserializeLatestEmail(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String) return DeserializeLatestEmail(root.GetString() ?? "null");
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? ReadString(string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                ? value.ToString() : null;
            var sentAtText = ReadString("sentAt");
            DateTimeOffset? sentAt = DateTimeOffset.TryParse(sentAtText, out var parsed) ? parsed : null;
            return new LatestEmail(ReadString("id"), sentAt, ReadString("from"), ReadString("subject"), ReadString("body"));
        }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<TicketSnapshot> DeserializeTickets(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String) return DeserializeTickets(root.GetString() ?? "null");
            if (root.ValueKind == JsonValueKind.Array)
                return JsonSerializer.Deserialize<List<TicketSnapshot>>(root.GetRawText(), JsonOptions) ?? [];
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var data) || root.TryGetProperty("Data", out data))
                    return data.ValueKind == JsonValueKind.Array
                        ? JsonSerializer.Deserialize<List<TicketSnapshot>>(data.GetRawText(), JsonOptions) ?? []
                        : [];
            }
        }
        catch (JsonException) { }
        return [];
    }
}
