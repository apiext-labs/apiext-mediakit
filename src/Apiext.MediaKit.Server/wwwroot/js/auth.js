// 浏览器端认证：由浏览器直接发起请求，确保 Set-Cookie 写入浏览器
window.apiextAuth = {
    // 成功返回 token 字符串；失败返回 "ERROR:<原因>"
    login: async function (username, password) {
        try {
            const resp = await fetch('/api/auth/login', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ username: username, password: password })
            });
            const data = await resp.json().catch(() => ({}));
            if (resp.ok) {
                return data.token || '';
            }
            return 'ERROR:' + (data.error || '登录失败');
        } catch (e) {
            return 'ERROR:网络错误';
        }
    },
    logout: async function () {
        try {
            await fetch('/api/auth/logout', { method: 'POST' });
        } catch (e) {
            // ignore
        }
    }
};

// 通用工具：复制文本到剪贴板（失败时降级 execCommand）
window.apiextUtil = {
    copy: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return 'OK';
        } catch (e) {
            try {
                const ta = document.createElement('textarea');
                ta.value = text;
                ta.style.position = 'fixed';
                ta.style.opacity = '0';
                document.body.appendChild(ta);
                ta.select();
                document.execCommand('copy');
                document.body.removeChild(ta);
                return 'OK';
            } catch (e2) {
                return 'ERROR:' + e2;
            }
        }
    },
    // 触发浏览器下载（content 为文本内容）
    download: function (filename, content, mimeType) {
        try {
            const blob = new Blob([content], { type: mimeType || 'application/octet-stream' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = filename;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            setTimeout(() => URL.revokeObjectURL(url), 1000);
            return 'OK';
        } catch (e) {
            return 'ERROR:' + e;
        }
    }
};
