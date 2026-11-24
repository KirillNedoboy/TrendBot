[CmdletBinding()]
param([switch]$ValidateOnly, [switch]$Telegram)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ssh = (Get-Command ssh -CommandType Application | Select-Object -First 1).Source
$receiver = @'
import decimal, hashlib, hmac, json, os, pathlib, re, signal, subprocess, sys, time, urllib.parse, urllib.request, uuid
base = pathlib.Path('/etc/credstore.encrypted')
owned = None
stored = False
deadline = time.monotonic() + 30

def remaining():
    value = deadline - time.monotonic()
    if value <= 0:
        raise TimeoutError('preflight-deadline')
    return min(8, value)

def expired(*_):
    raise TimeoutError('preflight-deadline')

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError('redirect-refused')

opener = urllib.request.build_opener(NoRedirect())

def output(value):
    print(json.dumps(value, separators=(',', ':')), flush=True)

def get_json(url, headers=None, data=None):
    request = urllib.request.Request(url, headers=headers or {}, data=data, method='GET' if data is None else 'POST')
    with opener.open(request, timeout=remaining()) as response:
        raw = response.read(131073)
    if len(raw) > 131072:
        raise ValueError('response-size')
    return json.loads(raw)

try:
    signal.signal(signal.SIGALRM, expired)
    signal.setitimer(signal.ITIMER_REAL, max(0.001, deadline - time.monotonic()))
    host_key = pathlib.Path('/var/lib/systemd/credential.secret')
    if host_key.is_symlink() or not host_key.is_file():
        raise ValueError('host-key-not-provisioned')
    packet = json.load(sys.stdin)
    telegram = set(packet) == {'telegramBotToken', 'telegramChatId'}
    if not telegram and set(packet) != {'apiKey', 'apiSecret'}:
        raise ValueError('credential-shape')
    for value in packet.values():
        if not isinstance(value, str) or not 1 <= len(value) <= 4096 or not value.isascii() or any(c.isspace() or ord(c) < 33 or ord(c) > 126 for c in value):
            raise ValueError('credential-shape')
    if telegram and (not re.fullmatch(r'[0-9]+:[A-Za-z0-9_-]{20,240}', packet['telegramBotToken'])
        or not re.fullmatch(r'-?[1-9][0-9]{0,19}', packet['telegramChatId'])):
        raise ValueError('telegram-credential-shape')
    os.umask(0o077)
    if base.is_symlink():
        raise ValueError('credential-directory')
    base.mkdir(mode=0o700, exist_ok=True)
    owned = base / (('tradingbot-telegram-' if telegram else 'tradingbot-bybit-demo-') + uuid.uuid4().hex)
    owned.mkdir(mode=0o700)
    names = [('telegramBotToken', 'telegram-bot-token'), ('telegramChatId', 'telegram-chat-id')] if telegram else [('apiKey', 'bybit-demo-api-key'), ('apiSecret', 'bybit-demo-api-secret')]
    for field, name in names:
        path = owned / (name + '.cred')
        result = subprocess.run(['systemd-creds', 'encrypt', '--with-key=host', '--name=' + name, '-', str(path)],
            input=packet[field].encode('ascii'), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=min(10, deadline - time.monotonic()))
        if result.returncode:
            raise ValueError('credential-encryption')
        path.chmod(0o600)
    (owned / 'ready').write_text('v1\n', encoding='ascii')
    stored = True
except Exception:
    if owned is not None and not stored:
        for name in ['bybit-demo-api-key.cred', 'bybit-demo-api-secret.cred', 'telegram-bot-token.cred', 'telegram-chat-id.cred', 'ready']:
            try:
                (owned / name).unlink()
            except FileNotFoundError:
                pass
        try:
            owned.rmdir()
        except OSError:
            pass
    output({'stored': False, 'ordersSent': False})
    sys.exit(2)

if telegram:
    report = {'stored': True, 'directory': str(owned), 'ordersSent': False, 'preflightPassed': False, 'testMessageSent': False}
    try:
        url = 'https://api.telegram.org/bot' + packet['telegramBotToken'] + '/'
        if not get_json(url + 'getMe').get('ok'):
            raise ValueError('telegram-bot-rejected')
        query = urllib.parse.urlencode({'chat_id': packet['telegramChatId']})
        if not get_json(url + 'getChat?' + query).get('ok'):
            raise ValueError('telegram-chat-rejected')
        message = 'TradingBot: уведомления о сигналах подключены. Исполнение — только Bybit Demo.'
        data = urllib.parse.urlencode({'chat_id': packet['telegramChatId'], 'text': message}).encode('ascii')
        report['testMessageSent'] = get_json(url + 'sendMessage', {'Content-Type': 'application/x-www-form-urlencoded'}, data).get('ok') is True
        report['preflightPassed'] = report['testMessageSent']
    except Exception:
        pass
    output(report)
    sys.exit(0)

report = {'stored': True, 'directory': str(owned), 'ordersSent': False, 'accountConnected': False, 'positionOrderScope': 'linear-USDT'}
try:
    start = time.monotonic()
    server = get_json('https://api-demo.bybit.com/v5/market/time')
    server_ms = int(server['result']['timeNano']) // 1000000
    origin = server_ms + int((time.monotonic() - start) * 500)
    origin_at = time.monotonic()
    def private_get(path, params):
        query = urllib.parse.urlencode(params)
        timestamp = str(origin + int((time.monotonic() - origin_at) * 1000))
        signature = hmac.new(packet['apiSecret'].encode('ascii'), (timestamp + packet['apiKey'] + '5000' + query).encode('ascii'), hashlib.sha256).hexdigest()
        response = get_json('https://api-demo.bybit.com' + path + ('?' + query if query else ''),
            {'X-BAPI-API-KEY': packet['apiKey'], 'X-BAPI-TIMESTAMP': timestamp, 'X-BAPI-RECV-WINDOW': '5000', 'X-BAPI-SIGN': signature})
        code = response.get('retCode')
        if code != 0:
            if isinstance(code, int):
                report['exchangeCode'] = code
            raise ValueError('demo-api-rejected')
        return response['result']
    account = private_get('/v5/account/info', [])
    report['accountConnected'] = True
    report['isolatedMargin'] = account.get('marginMode') == 'ISOLATED_MARGIN'
    def has_items(path, extra, positions=False):
        cursor = ''
        seen = set()
        for _ in range(1000):
            params = [('category','linear'), ('settleCoin','USDT')] + extra
            if cursor:
                params.append(('cursor', cursor))
            page = private_get(path, params)
            for row in page['list']:
                if not positions or decimal.Decimal(row['size']) > 0:
                    return True
            cursor = page.get('nextPageCursor')
            if cursor == '':
                return False
            if not isinstance(cursor, str) or cursor in seen:
                raise ValueError('demo-pagination')
            seen.add(cursor)
        raise ValueError('demo-pagination')
    report['hasOpenPositions'] = has_items('/v5/position/list', [], True)
    report['hasOpenOrders'] = has_items('/v5/order/realtime', [('openOnly','0')])
    report['preflightPassed'] = report['isolatedMargin'] and not report['hasOpenPositions'] and not report['hasOpenOrders']
except Exception:
    report['preflightPassed'] = False
output(report)

'@
$receiverBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($receiver))
$remote = "python3 -I -c `"import base64; exec(base64.b64decode('$receiverBase64'))`""

function ConvertTo-DemoCredentialPayload([string]$Key, [string]$Secret, [bool]$Consented, [bool]$ForTelegram = $false) {
    if (-not $Consented) {
        throw [ArgumentException]::new('Credential storage consent is required.', 'missingConsent')
    }
    $Key = $Key.Trim()
    $Secret = $Secret.Trim()
    if ($ForTelegram) {
        if ($Key -cnotmatch '^[0-9]+:[A-Za-z0-9_-]{20,240}$') {
            throw [ArgumentException]::new('The Telegram bot token format is invalid.', 'invalidKey')
        }
        if ($Secret -cnotmatch '^-?[1-9][0-9]{0,19}$') {
            throw [ArgumentException]::new('The Telegram chat identifier format is invalid.', 'invalidSecret')
        }
        return (@{ telegramBotToken = $Key; telegramChatId = $Secret } | ConvertTo-Json -Depth 2 -Compress)
    }
    if ($Key -notmatch '^[!-~]{1,4096}$') {
        throw [ArgumentException]::new('The API key format is invalid.', 'invalidKey')
    }
    if ($Secret -notmatch '^[!-~]{1,4096}$') {
        throw [ArgumentException]::new('The API secret format is invalid.', 'invalidSecret')
    }
    return (@{ apiKey = $Key; apiSecret = $Secret } | ConvertTo-Json -Depth 2 -Compress)
}

function Invoke-CredentialTransport([string]$Command, [string]$Payload) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $ssh
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in @('-T', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o',
        'ConnectTimeout=10', '-o', 'HostName=138.124.108.146', '-o', 'User=root', '-p', '22', 'short-bot', $Command)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'transport-start' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($Payload)
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            throw 'transport-timeout'
        }
        $null = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'transport-outcome-unknown' }
        $result = $stdout.GetAwaiter().GetResult()
    }
    finally { $process.Dispose() }
    return $result
}

if ($ValidateOnly) {
    $syntax = "python3 -I -c `"import ast,base64; ast.parse(base64.b64decode('$receiverBase64')); print('RECEIVER_SYNTAX_OK')`""
    $result = Invoke-CredentialTransport $syntax ''
    if ($result.Trim() -ne 'RECEIVER_SYNTAX_OK') { throw 'receiver-validation' }
    Write-Output 'SECURE_INPUT_VALIDATION_OK: no credentials or server mutations used'
    exit 0
}
if (-not $IsWindows) { throw 'This masked input window requires Windows.' }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$labels = @{
    title = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0J/QvtC00LrQu9GO0YfQtdC90LjQtSBCeWJpdCBEZW1v'))
    info = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0KHQtdGA0LLQtdGAOiAxMzguMTI0LjEwOC4xNDYK0JrQu9GO0YfQuCDQsdGD0LTRg9GCINC/0LXRgNC10LTQsNC90Ysg0L/QviBTU0gg0Lgg0YHQvtGF0YDQsNC90LXQvdGLINGC0L7Qu9GM0LrQviDQt9Cw0YjQuNGE0YDQvtCy0LDQvdC90YvQvNC4LgrQkdGD0LTQtdGCINC/0YDQvtCy0LXRgNC10L0g0LTQvtGB0YLRg9C/INC6IERlbW8t0YHRh9GR0YLRgy4g0KHQtNC10LvQutC4INC90LUg0L7RgtC/0YDQsNCy0LvRj9GO0YLRgdGPLg=='))
    key = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('QVBJIGtleSAoRGVtbyBUcmFkaW5nKQ=='))
    secret = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('QVBJIHNlY3JldCAoRGVtbyBUcmFkaW5nKQ=='))
    consent = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0KDQsNC30YDQtdGI0LDRjiDRgdC+0YXRgNCw0L3QuNGC0YwgRGVtby3QutC70Y7Rh9C4INC90LAg0YPQutCw0LfQsNC90L3QvtC8INGB0LXRgNCy0LXRgNC1INC4INC/0YDQvtCy0LXRgNC40YLRjCDRgdGH0ZHRgi4K0K3RgtC+INCd0JUg0YDQsNC30YDQtdGI0LXQvdC40LUg0L7RgtC60YDRi9Cy0LDRgtGMINGB0LTQtdC70LrQuC4='))
    save = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0J/QvtC00LrQu9GO0YfQuNGC0YwgRGVtbw=='))
    cancel = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0J7RgtC80LXQvdCw'))
    help = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0J7QsdCwINC/0L7Qu9GPINGB0LrRgNGL0YLRiy4g0JLRgdGC0LDQstGM0YLQtSDQt9C90LDRh9C10L3QuNGPINC40LcgRGVtbyBUcmFkaW5nLiDQn9GA0L7QsdC10LvRiyDQuCDQv9C10YDQtdC90L7RgdGLINC/0L4g0LrRgNCw0Y/QvCDQsdGD0LTRg9GCINGD0LHRgNCw0L3RiyDQsNCy0YLQvtC80LDRgtC40YfQtdGB0LrQuC4='))
    invalidKey = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0J/QvtC70LUgQVBJIGtleTog0L/Rg9GB0YLQvtC1INC30L3QsNGH0LXQvdC40LUsINC/0YDQvtCx0LXQuyDQstC90YPRgtGA0Lgg0LjQu9C4INC90LXQtNC+0L/Rg9GB0YLQuNC80YvQuSDRgdC40LzQstC+0LsuINCh0LrQvtC/0LjRgNGD0LnRgtC1INC90LDRgdGC0L7Rj9GJ0LjQuSDQutC70Y7RhyDQutC90L7Qv9C60L7QuSBCeWJpdC4='))
    invalidSecret = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0J/QvtC70LUgQVBJIHNlY3JldDog0L/Rg9GB0YLQvtC1INC30L3QsNGH0LXQvdC40LUsINC/0YDQvtCx0LXQuyDQstC90YPRgtGA0Lgg0LjQu9C4INC90LXQtNC+0L/Rg9GB0YLQuNC80YvQuSDRgdC40LzQstC+0LsuINCd0YPQttC10L0g0L3QsNGB0YLQvtGP0YnQuNC5INGB0LXQutGA0LXRgiwg0L3QtSDQvNCw0YHQutCwLg=='))
    missingConsent = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0J/QvtGB0YLQsNCy0YzRgtC1INCz0LDQu9C+0YfQutGDINGB0L7Qs9C70LDRgdC40Y8g0L3QsCDRgdC+0YXRgNCw0L3QtdC90LjQtSDQutC70Y7Rh9C10Lkg0Lgg0L/RgNC+0LLQtdGA0LrRgyBEZW1vLdGB0YfRkdGC0LAuINCt0YLQviDQvdC1INGA0LDQt9GA0LXRiNC10L3QuNC1INC90LAg0YHQtNC10LvQutC4Lg=='))
    pending = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0J/QvtC00LrQu9GO0YfQtdC90LjQtS4uLiDQntC60L3QviDQvNC+0LbQtdGCINC90LUg0L7RgtCy0LXRh9Cw0YLRjCDQtNC+IDYwINGB0LXQutGD0L3QtC4='))
    unknown = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0KDQtdC30YPQu9GM0YLQsNGCINC/0LXRgNC10LTQsNGH0Lgg0L3QtSDQv9C+0LTRgtCy0LXRgNC20LTRkdC9LiDQntGC0LzQtdC90LjRgtC1INC+0LrQvdC+INC4INGB0L7QvtCx0YnQuNGC0LUg0LDRgdGB0LjRgdGC0LXQvdGC0YMuCtCf0L7QstGC0L7RgNC90LDRjyDQvtGC0L/RgNCw0LLQutCwINC+0YLQutC70Y7Rh9C10L3QsDsg0YHQtNC10LvQutC4INC90LUg0L7RgtC/0YDQsNCy0LvRj9C70LjRgdGMLg=='))
}
if ($Telegram) {
    $labels.info = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0KHQtdGA0LLQtdGAOiAxMzguMTI0LjEwOC4xNDYuINCd0LDRgdGC0YDQvtC50LrQuCDRgdC+0YXRgNCw0L3Rj9GO0YLRgdGPINGC0L7Qu9GM0LrQviDQt9Cw0YjQuNGE0YDQvtCy0LDQvdC90YvQvNC4LgrQkdGD0LTQtdGCINC+0YLQv9GA0LDQstC70LXQvdC+INC+0LTQvdC+INGB0L7QvtCx0YnQtdC90LjQtSDQsiDRg9C60LDQt9Cw0L3QvdGL0Lkg0YfQsNGCOgrCq1RyYWRpbmdCb3Q6INGD0LLQtdC00L7QvNC70LXQvdC40Y8g0L4g0YHQuNCz0L3QsNC70LDRhSDQv9C+0LTQutC70Y7Rh9C10L3Riy4g0JjRgdC/0L7Qu9C90LXQvdC40LUg4oCUINGC0L7Qu9GM0LrQviBCeWJpdCBEZW1vLsK7'))
    $labels.key = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0KLQvtC60LXQvSDQstCw0YjQtdCz0L4gVGVsZWdyYW0t0LHQvtGC0LAgKEJvdEZhdGhlcik='))
    $labels.secret = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0KfQuNGB0LvQvtCy0L7QuSBUZWxlZ3JhbSBDaGF0IElEICjQstC60LvRjtGH0LDRjyDQvNC40L3Rg9GBINC00LvRjyDQs9GA0YPQv9C/0Ysp'))
    $labels.consent = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0KDQsNC30YDQtdGI0LDRjiDRgdC+0YXRgNCw0L3QuNGC0Ywg0L3QsNGB0YLRgNC+0LnQutC4IFRlbGVncmFtINC90LAg0Y3RgtC+0Lwg0YHQtdGA0LLQtdGA0LUsINC+0YLQv9GA0LDQstC40YLRjCDRg9C60LDQt9Cw0L3QvdC+0LUg0YLQtdGB0YLQvtCy0L7QtSDRgdC+0L7QsdGJ0LXQvdC40LUg0Lgg0LTQsNC70LXQtSDRg9Cy0LXQtNC+0LzQu9C10L3QuNGPINC+INGB0LjQs9C90LDQu9Cw0YUg0LIg0Y3RgtC+0YIg0YfQsNGCLg=='))
    $labels.save = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0J/QvtC00LrQu9GO0YfQuNGC0YwgVGVsZWdyYW0='))
    $labels.help = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0J7QsdCwINC/0L7Qu9GPINGB0LrRgNGL0YLRiy4g0J7RgtC60YDQvtC50YLQtSDQstCw0YjQtdCz0L4g0LHQvtGC0LAg0LIgVGVsZWdyYW0g0Lgg0L3QsNC20LzQuNGC0LUgwqvQl9Cw0L/Rg9GB0YLQuNGC0YzCuy4g0JLRgdGC0LDQstGM0YLQtSDRgtC+0LrQtdC9IEJvdEZhdGhlciDQuCDRh9C40YHQu9C+0LLQvtC5IElEINC90YPQttC90L7Qs9C+INGH0LDRgtCwLg=='))
    $labels.invalidKey = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0J3Rg9C20LXQvSDQvdCw0YHRgtC+0Y/RidC40Lkg0YLQvtC60LXQvSBCb3RGYXRoZXI6INGH0LjRgdC70L4sINC00LLQvtC10YLQvtGH0LjQtSDQuCDRgdC10LrRgNC10YLQvdCw0Y8g0YfQsNGB0YLRjC4g0J/RgNC+0LHQtdC70Ysg0LLQvdGD0YLRgNC4INC4INC80LDRgdC60LAg0L3QtdC00L7Qv9GD0YHRgtC40LzRiy4='))
    $labels.invalidSecret = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0J3Rg9C20LXQvSDRh9C40YHQu9C+0LLQvtC5IENoYXQgSUQ6INGC0L7Qu9GM0LrQviDRhtC40YTRgNGLLCDQtNC70Y8g0LPRgNGD0L/Qv9GLINCy0L7Qt9C80L7QttC10L0g0LzQuNC90YPRgSDQsiDQvdCw0YfQsNC70LUu'))
    $labels.missingConsent = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('0JTQu9GPINC/0L7QtNC60LvRjtGH0LXQvdC40Y8g0YLRgNC10LHRg9C10YLRgdGPINCy0LDRiNC1INGB0L7Qs9C70LDRgdC40LUg0L3QsCDRhdGA0LDQvdC10L3QuNC1INC90LDRgdGC0YDQvtC10Log0Lgg0L7RgtC/0YDQsNCy0LrRgyDRg9C60LDQt9Cw0L3QvdC+0LPQviDRgdC+0L7QsdGJ0LXQvdC40Y8g0Lgg0YHQuNCz0L3QsNC70L7Qsi4='))
}
$form = [Windows.Forms.Form]::new()
$form.Text = if ($Telegram) { 'Telegram - secure credentials' } else { 'Bybit Demo - secure credentials' }
$form.StartPosition = 'CenterScreen'
$form.ClientSize = [Drawing.Size]::new(670, 470)
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.Font = [Drawing.Font]::new('Segoe UI', 10)
$info = [Windows.Forms.Label]::new()
$info.Text = $labels.info
$info.SetBounds(22, 18, 630, 86)
$form.Controls.Add($info)
$keyLabel = [Windows.Forms.Label]::new()
$keyLabel.Text = $labels.key
$keyLabel.SetBounds(22, 110, 620, 25)
$form.Controls.Add($keyLabel)
$keyBox = [Windows.Forms.TextBox]::new()
$keyBox.UseSystemPasswordChar = $true
$keyBox.SetBounds(22, 137, 620, 30)
$form.Controls.Add($keyBox)
$secretLabel = [Windows.Forms.Label]::new()
$secretLabel.Text = $labels.secret
$secretLabel.SetBounds(22, 180, 620, 25)
$form.Controls.Add($secretLabel)
$secretBox = [Windows.Forms.TextBox]::new()
$secretBox.UseSystemPasswordChar = $true
$secretBox.SetBounds(22, 207, 620, 30)
$form.Controls.Add($secretBox)
$consent = [Windows.Forms.CheckBox]::new()
$consent.Text = $labels.consent
$consent.SetBounds(22, 254, 620, 52)
$consent.Checked = $false
$form.Controls.Add($consent)
$status = [Windows.Forms.Label]::new()
$status.Text = $labels.help
$status.SetBounds(22, 315, 620, 65)
$form.Controls.Add($status)
$save = [Windows.Forms.Button]::new()
$save.Text = $labels.save
$save.SetBounds(22, 398, 280, 42)
$form.Controls.Add($save)
$cancel = [Windows.Forms.Button]::new()
$cancel.Text = $labels.cancel
$cancel.SetBounds(362, 398, 280, 42)
$cancel.DialogResult = [Windows.Forms.DialogResult]::Cancel
$form.Controls.Add($cancel)
$form.CancelButton = $cancel
$state = [pscustomobject]@{ Report = $null; Attempted = $false }
$save.Add_Click({
    if ($state.Attempted) { return }
    try {
        $payload = ConvertTo-DemoCredentialPayload $keyBox.Text $secretBox.Text $consent.Checked $Telegram.IsPresent
    }
    catch [ArgumentException] {
        $issue = $_.Exception.ParamName
        $status.Text = $labels[$issue]
        if ($issue -eq 'invalidKey') { $keyBox.SelectAll(); $null = $keyBox.Focus() }
        elseif ($issue -eq 'invalidSecret') { $secretBox.SelectAll(); $null = $secretBox.Focus() }
        else { $null = $consent.Focus() }
        return
    }
    $state.Attempted = $true
    $save.Enabled = $false
    $cancel.Enabled = $false
    $status.Text = $labels.pending
    $status.Refresh()
    $keyBox.ReadOnly = $true
    $secretBox.ReadOnly = $true
    $consent.Enabled = $false
    $keyBox.Clear()
    $secretBox.Clear()
    try {
        $response = Invoke-CredentialTransport $remote $payload
        $parsed = $response | ConvertFrom-Json
        if (-not $parsed.stored) { throw 'credential-handoff-unconfirmed' }
        $state.Report = $parsed
        $form.DialogResult = [Windows.Forms.DialogResult]::OK
        $form.Close()
    }
    catch {
        $status.Text = $labels.unknown
        $cancel.Enabled = $true
    }
    finally { $payload = $null; $response = $null }
})
$form.Add_Shown({ [Console]::WriteLine('SECURE_INPUT_READY') })
try { $null = $form.ShowDialog() }
finally { $keyBox.Clear(); $secretBox.Clear(); $form.Dispose() }
if ($null -ne $state.Report) {
    $state.Report | ConvertTo-Json -Depth 3 -Compress
    if (-not $state.Report.preflightPassed) { exit 3 }
    exit 0
}
if ($state.Attempted) { Write-Output 'CREDENTIAL_HANDOFF_UNCONFIRMED'; exit 2 }
Write-Output 'CREDENTIAL_INPUT_CANCELLED'
exit 0
