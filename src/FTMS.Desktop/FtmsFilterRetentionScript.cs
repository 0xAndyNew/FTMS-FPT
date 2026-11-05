namespace FTMS.Desktop;

internal static class FtmsFilterRetentionScript
{
    internal const string Value = """
        (() => {
          if (window.__ftmsCompanionFilterRetentionInstalled) return;
          window.__ftmsCompanionFilterRetentionInstalled = true;

          const norm = str => String(str || '').normalize('NFD')
            .replace(/[̀-ͯ]/g, '')
            .replace(/[đĐ]/g, 'd')
            .toLowerCase()
            .trim();

          let manualOverride = false;
          let myTicketState = false;

          const isMyTicketActive = btn => {
            if (!btn) return false;
            if (btn.classList.contains('ant-switch-checked')) return true;
            if (btn.getAttribute('aria-checked') === 'true') return true;
            if (btn.getAttribute('aria-pressed') === 'true') return true;
            if (btn.classList.contains('active') || btn.classList.contains('selected') || btn.classList.contains('ant-btn-primary')) return true;
            if (btn.querySelector('.ant-switch-checked, [aria-checked="true"], .active, .selected')) return true;
            return false;
          };

          const isMyTicketButton = el => {
            if (!el) return false;
            if (el.closest('header, nav, .main-header, .navbar, #header, .header')) return false;

            const btn = el.closest('button, [role="button"], a, .btn, .ant-switch, [role="switch"], label, [class*="pointer"]');
            if (!btn) return false;
            if (btn.closest('header, nav, .main-header, .navbar, #header, .header, .dropdown-menu')) return false;

            const textProps = [
              btn.getAttribute('title'),
              btn.getAttribute('aria-label'),
              btn.getAttribute('data-tooltip'),
              btn.getAttribute('data-original-title'),
              btn.innerText
            ].map(norm).join(' ');

            if (textProps.includes('cua toi') || textProps.includes('my ticket') || textProps.includes('ca nhan'))
              return true;

            const allBtns = Array.from(document.querySelectorAll('button, [role="button"], a, .btn'));
            const vmBtn = allBtns.find(b => norm(b.innerText).includes('vung mien'));
            if (vmBtn) {
              const vmIdx = allBtns.indexOf(vmBtn);
              const btnIdx = allBtns.indexOf(btn);
              if (btnIdx > vmIdx && btnIdx <= vmIdx + 2) {
                const text = norm(btn.innerText);
                if (!text.includes('vung mien') && !text.includes('trang thai') && !text.includes('tim kiem'))
                  return true;
              }
              if (vmBtn.parentElement) {
                const siblings = Array.from(vmBtn.parentElement.querySelectorAll('button, [role="button"], a, .btn'));
                const vmSiblingIdx = siblings.indexOf(vmBtn);
                if (vmSiblingIdx !== -1 && siblings[vmSiblingIdx + 1] === btn) return true;
              }
            }

            const icon = btn.querySelector('i, svg, [class*="user"], [class*="person"], [class*="avatar"]');
            if (icon) {
              const iconBox = icon.closest('[class*="user"], [class*="person"], [class*="avatar"]') || icon;
              const cls = norm(iconBox.getAttribute('class') || '') + ' ' + norm(btn.getAttribute('class') || '');
              if (cls.includes('user') || cls.includes('person') || cls.includes('avatar') || cls.includes('profile'))
                return true;
              if (icon.tagName && icon.tagName.toLowerCase() === 'svg') {
                if (vmBtn && allBtns.indexOf(btn) > allBtns.indexOf(vmBtn) && allBtns.indexOf(btn) <= allBtns.indexOf(vmBtn) + 2)
                  return true;
              }
            }

            return false;
          };

          const isStatusToggleButton = el => {
            if (!el) return false;
            const btn = el.closest('button, [role="button"], a, .btn');
            if (!btn) return false;
            const text = norm(btn.innerText || btn.getAttribute('title') || '');
            return text.includes('trang thai') && !text.includes('vung mien');
          };

          const isStatusDropdownElement = el => {
            if (!el) return false;
            const container = el.closest('.dropdown-menu, [role="listbox"], [role="menu"], .ant-select-dropdown, .ant-dropdown, div[class*="dropdown"], div[class*="popover"]');
            if (!container) return false;
            const text = norm(container.innerText);
            return text.includes('tat ca') && text.includes('moi') && text.includes('phan cong');
          };

          const updateStatusButtonLabel = () => {
            const elements = Array.from(document.querySelectorAll('button, [role="button"], a, .btn, .ant-select, [class*="select"]'))
              .filter(el => !el.closest('header, nav, .main-header, .navbar, #header, .header'));

            for (const btn of elements) {
              const text = norm(btn.innerText || '');
              const isStatusBtn = (text.includes('trang thai') && !text.includes('vung mien')) ||
                                  (text === 'tat ca' || text.startsWith('tat ca (') || text.startsWith('tat ca(')) ||
                                  (btn.getAttribute('title') && norm(btn.getAttribute('title')).includes('trang thai'));
              if (!isStatusBtn) continue;

              const badge = btn.querySelector('.badge, [class*="badge"], [class*="count"], .ant-scroll-number');
              if (badge && badge.innerText !== '2') {
                badge.innerText = '2';
              }

              let updated = false;
              for (const node of btn.childNodes) {
                if (node.nodeType === Node.TEXT_NODE) {
                  const val = node.textContent || '';
                  if (/trạng\s*thái(?:\s*\([^)]*\))?/i.test(val)) {
                    node.textContent = val.replace(/trạng\s*thái(?:\s*\([^)]*\))?/i, 'Trạng thái (2)');
                    updated = true;
                  } else if (/tất\s*cả(?:\s*\([^)]*\))?/i.test(val)) {
                    node.textContent = val.replace(/tất\s*cả(?:\s*\([^)]*\))?/i, 'Trạng thái (2)');
                    updated = true;
                  }
                }
              }
              if (!updated) {
                const children = btn.querySelectorAll('span, div');
                for (const child of children) {
                  if (!child.children.length) {
                    const val = child.innerText || '';
                    if (/trạng\s*thái(?:\s*\([^)]*\))?/i.test(val)) {
                      child.innerText = val.replace(/trạng\s*thái(?:\s*\([^)]*\))?/i, 'Trạng thái (2)');
                      break;
                    } else if (/tất\s*cả(?:\s*\([^)]*\))?/i.test(val)) {
                      child.innerText = val.replace(/tất\s*cả(?:\s*\([^)]*\))?/i, 'Trạng thái (2)');
                      break;
                    }
                  }
                }
              }
            }
          };

          // Assign checkmark to Mới and Phân công only, uncheck others (DOM only, NO network payload mutation)
          const applyStatusCheckmarks = () => {
            updateStatusButtonLabel();

            const containers = document.querySelectorAll('.dropdown-menu, [role="listbox"], [role="menu"], .ant-select-dropdown, .ant-dropdown, div[class*="dropdown"], div[class*="popover"]');
            for (const container of containers) {
              const text = norm(container.innerText);
              if (text.includes('tat ca') && text.includes('moi') && text.includes('phan cong')) {
                const items = Array.from(container.querySelectorAll('li, label, div, [role="option"], tr'));
                for (const item of items) {
                  const itemText = norm(item.innerText);
                  const checkbox = item.querySelector('input[type="checkbox"], [role="checkbox"]') ||
                                    (item.getAttribute('role') === 'checkbox' ? item : null);

                  const isTatCa = itemText.startsWith('tat ca');
                  const isMoi = itemText.startsWith('moi') || (itemText.includes('moi') && !itemText.includes('lam moi'));
                  const isPhanCong = itemText.startsWith('phan cong') || itemText.includes('phan cong');

                  const shouldBeChecked = isMoi || isPhanCong;
                  if (checkbox) {
                    checkbox.checked = shouldBeChecked;
                    checkbox.setAttribute('aria-checked', shouldBeChecked ? 'true' : 'false');

                    try {
                      const proto = window.HTMLInputElement?.prototype;
                      const desc = Object.getOwnPropertyDescriptor(proto, 'checked');
                      if (desc && desc.set) {
                        desc.set.call(checkbox, shouldBeChecked);
                      }
                    } catch { }

                    try { checkbox.dispatchEvent(new Event('input', { bubbles: true })); } catch { }
                    try { checkbox.dispatchEvent(new Event('change', { bubbles: true })); } catch { }

                    if (shouldBeChecked) {
                      checkbox.classList.add('checked', 'ant-checkbox-checked');
                      item.classList.add('active', 'selected', 'ant-select-item-option-selected');
                    } else {
                      checkbox.classList.remove('checked', 'ant-checkbox-checked');
                      item.classList.remove('active', 'selected', 'ant-select-item-option-selected');
                    }
                  }
                }
              }
            }

            try {
              if (globalThis.jQuery) {
                const $ = globalThis.jQuery;
                $('[name*="status" i], [id*="status" i]').each(function() {
                  const widget = $(this).data('kendoMultiSelect') || $(this).data('kendoDropDownList');
                  if (widget && typeof widget.value === 'function') {
                    widget.value([0, 1]);
                    widget.trigger('change');
                  }
                });
              }
            } catch { }
          };

          const ensureDropdownRenderedAndChecked = () => {
            const containers = document.querySelectorAll('.dropdown-menu, [role="listbox"], [role="menu"], .ant-select-dropdown, .ant-dropdown, div[class*="dropdown"], div[class*="popover"]');
            let hasStatusDropdown = false;
            for (const c of containers) {
              const text = norm(c.innerText);
              if (text.includes('tat ca') && text.includes('moi') && text.includes('phan cong')) {
                hasStatusDropdown = true;
                break;
              }
            }

            if (hasStatusDropdown) {
              applyStatusCheckmarks();
              return;
            }

            const allBtns = Array.from(document.querySelectorAll('button, [role="button"], a, .btn'));
            const statusBtn = allBtns.find(isStatusToggleButton);
            if (statusBtn) {
              statusBtn.click();
              setTimeout(() => {
                applyStatusCheckmarks();
                statusBtn.click();
              }, 60);
            }
          };

          // Trigger standard reload on FTMS page without modifying network payloads
          const triggerReload = () => {
            // 1. React search button on /ihub/react/list
            const listSearchBtn = document.querySelector('button.absolute.right-1, input[placeholder*="Tìm kiếm" i] ~ button, input[placeholder*="Tìm kiếm" i] + button');
            if (listSearchBtn && listSearchBtn.offsetParent !== null) {
              listSearchBtn.click();
              return;
            }

            // 2. Kendo Grid refresh on /ihub/list
            const kendoRefresh = document.querySelector('a.k-pager-refresh.k-link');
            if (kendoRefresh && kendoRefresh.offsetParent !== null) {
              kendoRefresh.click();
              return;
            }

            // 3. Kendo DataSource read
            try {
              const kendoGrid = globalThis.jQuery?.('#list-grid').data('kendoGrid');
              if (kendoGrid?.dataSource && typeof kendoGrid.dataSource.read === 'function') {
                kendoGrid.dataSource.read();
                return;
              }
            } catch { }

            // 4. Any "Làm mới" button in toolbar
            const reloadBtn = Array.from(document.querySelectorAll('button, [role="button"], a, .btn'))
              .find(b => {
                const text = norm(b.innerText || b.getAttribute('title') || '');
                return text.includes('lam moi') && !text.includes('vung mien');
              });
            if (reloadBtn && reloadBtn.offsetParent !== null) {
              reloadBtn.click();
            }
          };

          // Capture click events
          document.addEventListener('click', e => {
            if (isMyTicketButton(e.target)) {
              const btn = e.target.closest('button, [role="button"], a, .btn, .ant-switch, [role="switch"], label, [class*="pointer"]');
              const wasActive = isMyTicketActive(btn);

              setTimeout(() => {
                const isNowActive = isMyTicketActive(btn);
                const wasTurnedOff = (wasActive && !isNowActive) || (!isNowActive && myTicketState);
                myTicketState = isNowActive;

                if (wasTurnedOff) {
                  manualOverride = false;
                  applyStatusCheckmarks();
                  ensureDropdownRenderedAndChecked();
                  setTimeout(() => {
                    applyStatusCheckmarks();
                    triggerReload();
                  }, 300);
                } else {
                  updateStatusButtonLabel();
                }
              }, 100);
              return;
            }

            if (isStatusToggleButton(e.target)) {
              if (!manualOverride) {
                setTimeout(applyStatusCheckmarks, 50);
                setTimeout(applyStatusCheckmarks, 150);
              }
              return;
            }

            // User explicitly clicking dropdown items
            if (e.isTrusted && isStatusDropdownElement(e.target)) {
              const targetText = norm(e.target.innerText || e.target.closest('li, label, div, [role="option"]')?.innerText || '');
              const isOtherStatus = targetText.includes('dang thuc hien') || targetText.includes('hoan thanh') ||
                                    targetText.includes('tam ngung') || targetText.includes('dong') || targetText.includes('huy') ||
                                    targetText.startsWith('tat ca');
              if (isOtherStatus) {
                manualOverride = true;
              }
            }
          }, true);

          // Wrap fetch (observability only, NEVER touches body or headers)
          const origFetch = window.fetch;
          if (typeof origFetch === 'function') {
            window.fetch = function() {
              const promise = origFetch.apply(this, arguments);
              if (promise && typeof promise.then === 'function') {
                promise.then(() => {
                  if (!manualOverride) setTimeout(updateStatusButtonLabel, 200);
                }).catch(() => {});
              }
              return promise;
            };
          }

          // Wrap XMLHttpRequest (observability only, NEVER touches body or headers)
          const OrigXHR = window.XMLHttpRequest;
          if (typeof OrigXHR === 'function') {
            const origSend = OrigXHR.prototype.send;
            OrigXHR.prototype.send = function() {
              this.addEventListener('loadend', () => {
                if (!manualOverride) setTimeout(updateStatusButtonLabel, 200);
              }, { once: true });
              return origSend.apply(this, arguments);
            };
          }

          // Initial label update on load
          setTimeout(ensureDropdownRenderedAndChecked, 500);
          setTimeout(applyStatusCheckmarks, 1000);
          setTimeout(updateStatusButtonLabel, 500);
          setTimeout(updateStatusButtonLabel, 1500);
        })();
        """;
}
