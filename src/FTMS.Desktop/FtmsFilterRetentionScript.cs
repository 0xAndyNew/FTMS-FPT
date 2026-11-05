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
          let isAutomatedOpening = false;

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
            if (manualOverride || myTicketState) return;

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

          const getStatusType = el => {
            if (!el || el.children.length > 3) return null;
            const raw = norm(el.innerText || '');
            if (!raw) return null;
            const text = raw.replace(/^[•\s\-_*]+/, '').trim();

            if (text === 'moi' || text.startsWith('moi (') || text.startsWith('moi(')) return 'moi';
            if (text === 'phan cong' || text.startsWith('phan cong (') || text.startsWith('phan cong(')) return 'phan cong';

            if (text === 'tam ngung' || text === 'dang thuc hien' || text === 'hoan thanh' ||
                text === 'da dong' || text === 'dong' || text === 'da huy' || text === 'huy' ||
                text === 'chua xu ly' || text === 'khong xu ly') {
              return 'other';
            }
            return null;
          };

          const findTicketContainer = tagEl => {
            let curr = tagEl.parentElement;
            let candidate = null;
            while (curr && curr !== document.body && !curr.closest('header, nav, .ant-dropdown, .dropdown-menu')) {
              if (curr.tagName === 'TR' || curr.classList.contains('ant-list-item') || curr.classList.contains('ant-card') || curr.getAttribute('role') === 'row') {
                return curr;
              }
              const text = curr.innerText || '';
              const codes = text.match(/(?:RQ|CA|IN)\d{8,}/g) || [];
              if (codes.length === 1) {
                candidate = curr;
              } else if (codes.length > 1) {
                if (candidate) return candidate;
              }
              curr = curr.parentElement;
            }
            return candidate || tagEl.closest('tr, [class*="card"], div[class*="item"], div[class*="rounded"]');
          };

          // Filter DOM ticket list to strictly display only tickets in Mới and Phân công statuses
          const filterDomTicketRows = () => {
            if (manualOverride || myTicketState) {
              const hidden = document.querySelectorAll('[data-ftms-status-hidden="true"]');
              for (const el of hidden) {
                el.style.display = '';
                delete el.dataset.ftmsStatusHidden;
              }
              return;
            }

            const tags = document.querySelectorAll('.ant-tag, [class*="tag"], [class*="badge"], span[class*="rounded"], div[class*="rounded"]');
            for (const tag of tags) {
              if (tag.closest('header, nav, .ant-dropdown, .dropdown-menu, .ant-select-dropdown')) continue;

              const statusType = getStatusType(tag);
              if (!statusType) continue;

              const container = findTicketContainer(tag);
              if (!container || container === document.body) continue;

              if (statusType === 'other') {
                container.style.display = 'none';
                container.dataset.ftmsStatusHidden = 'true';
              } else if (statusType === 'moi' || statusType === 'phan cong') {
                if (container.dataset.ftmsStatusHidden === 'true') {
                  container.style.display = '';
                  delete container.dataset.ftmsStatusHidden;
                }
              }
            }

            // Update pager info if visible items count differs
            const pagers = document.querySelectorAll('.ant-pagination-total-text, .k-pager-info, [class*="pagination"]');
            for (const pager of pagers) {
              const text = pager.innerText || '';
              const match = /(\d+)-(\d+)\s+của\s+(\d+)\s+mục/i.exec(text);
              if (match) {
                const hiddenCount = document.querySelectorAll('[data-ftms-status-hidden="true"]').length;
                if (hiddenCount > 0) {
                  const total = parseInt(match[3], 10);
                  const visible = Math.max(1, total - hiddenCount);
                  pager.innerText = `1-${visible} của ${visible} mục (2 trạng thái)`;
                }
              }
            }
          };

          const isItemChecked = el => {
            if (!el) return false;
            if (el.querySelector('.ant-checkbox-checked, [aria-checked="true"]')) return true;
            if (el.classList.contains('ant-checkbox-checked') || el.classList.contains('selected') || el.classList.contains('active')) return true;
            if (el.classList.contains('ant-select-item-option-selected')) return true;
            const cb = el.querySelector('input[type="checkbox"]');
            if (cb && cb.checked) return true;
            return false;
          };

          const clickDropdownItem = el => {
            if (!el) return;
            const clickable = el.querySelector('label.ant-checkbox-wrapper, .ant-checkbox-wrapper, .ant-checkbox, input[type="checkbox"]') || el;
            clickable.click();
          };

          const applyStatusSelectionToContainer = container => {
            if (!container) return;
            const items = Array.from(container.querySelectorAll('li, label, div, [role="option"], tr'));

            let tatCaItem = null;
            let moiItem = null;
            let phanCongItem = null;
            const otherItems = [];

            for (const item of items) {
              const itemText = norm(item.innerText);
              if (itemText.startsWith('tat ca')) {
                tatCaItem = item;
              } else if (itemText.startsWith('moi') || (itemText.includes('moi') && !itemText.includes('lam moi'))) {
                moiItem = item;
              } else if (itemText.startsWith('phan cong') || itemText.includes('phan cong')) {
                phanCongItem = item;
              } else if (itemText.includes('dang thuc hien') || itemText.includes('hoan thanh') ||
                         itemText.includes('tam ngung') || itemText.includes('dong') || itemText.includes('huy')) {
                otherItems.push(item);
              }
            }

            // 1. If Tất cả is checked -> click to uncheck
            if (tatCaItem && isItemChecked(tatCaItem)) {
              clickDropdownItem(tatCaItem);
            }

            // 2. If Mới is NOT checked -> click to check
            if (moiItem && !isItemChecked(moiItem)) {
              clickDropdownItem(moiItem);
            }

            // 3. If Phân công is NOT checked -> click to check
            if (phanCongItem && !isItemChecked(phanCongItem)) {
              clickDropdownItem(phanCongItem);
            }

            // 4. If any other status is checked -> click to uncheck
            for (const other of otherItems) {
              if (isItemChecked(other)) {
                clickDropdownItem(other);
              }
            }
          };

          const runStatusSelectionWorkflow = () => {
            if (manualOverride || myTicketState) return;

            const containers = Array.from(document.querySelectorAll('.dropdown-menu, [role="listbox"], [role="menu"], .ant-select-dropdown, .ant-dropdown, div[class*="dropdown"], div[class*="popover"]'))
              .filter(c => {
                const text = norm(c.innerText);
                return text.includes('tat ca') && text.includes('moi') && text.includes('phan cong');
              });

            if (containers.length > 0) {
              applyStatusSelectionToContainer(containers[0]);
              updateStatusButtonLabel();
              filterDomTicketRows();
              return;
            }

            const allBtns = Array.from(document.querySelectorAll('button, [role="button"], a, .btn'));
            const statusBtn = allBtns.find(isStatusToggleButton);
            if (!statusBtn) {
              updateStatusButtonLabel();
              filterDomTicketRows();
              return;
            }

            isAutomatedOpening = true;
            statusBtn.click();

            setTimeout(() => {
              const openedContainers = Array.from(document.querySelectorAll('.dropdown-menu, [role="listbox"], [role="menu"], .ant-select-dropdown, .ant-dropdown, div[class*="dropdown"], div[class*="popover"]'))
                .filter(c => {
                  const text = norm(c.innerText);
                  return text.includes('tat ca') && text.includes('moi') && text.includes('phan cong');
                });

              if (openedContainers.length > 0) {
                applyStatusSelectionToContainer(openedContainers[0]);
              }

              setTimeout(() => {
                statusBtn.click();
                isAutomatedOpening = false;
                updateStatusButtonLabel();
                filterDomTicketRows();

                setTimeout(triggerReload, 150);
              }, 100);
            }, 120);
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
                  filterDomTicketRows();
                  updateStatusButtonLabel();
                  setTimeout(runStatusSelectionWorkflow, 150);
                } else {
                  filterDomTicketRows();
                  updateStatusButtonLabel();
                }
              }, 100);
              return;
            }

            if (isStatusToggleButton(e.target)) {
              if (!manualOverride && !isAutomatedOpening) {
                setTimeout(() => {
                  const containers = Array.from(document.querySelectorAll('.dropdown-menu, [role="listbox"], [role="menu"], .ant-select-dropdown, .ant-dropdown, div[class*="dropdown"], div[class*="popover"]'))
                    .filter(c => {
                      const text = norm(c.innerText);
                      return text.includes('tat ca') && text.includes('moi') && text.includes('phan cong');
                    });
                  if (containers.length > 0) {
                    applyStatusSelectionToContainer(containers[0]);
                  }
                }, 80);
              }
              return;
            }

            // User explicitly clicking dropdown items
            if (e.isTrusted && isStatusDropdownElement(e.target) && !isAutomatedOpening) {
              const targetText = norm(e.target.innerText || e.target.closest('li, label, div, [role="option"]')?.innerText || '');
              const isOtherStatus = targetText.includes('dang thuc hien') || targetText.includes('hoan thanh') ||
                                    targetText.includes('tam ngung') || targetText.includes('dong') || targetText.includes('huy') ||
                                    targetText.startsWith('tat ca');
              if (isOtherStatus) {
                manualOverride = true;
                filterDomTicketRows();
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
                  if (!manualOverride) {
                    setTimeout(() => {
                      updateStatusButtonLabel();
                      filterDomTicketRows();
                    }, 200);
                    setTimeout(() => {
                      updateStatusButtonLabel();
                      filterDomTicketRows();
                    }, 600);
                  }
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
                if (!manualOverride) {
                  setTimeout(() => {
                    updateStatusButtonLabel();
                    filterDomTicketRows();
                  }, 200);
                  setTimeout(() => {
                    updateStatusButtonLabel();
                    filterDomTicketRows();
                  }, 600);
                }
              }, { once: true });
              return origSend.apply(this, arguments);
            };
          }

          // Periodic background sync to guarantee DOM consistency
          setInterval(() => {
            if (!manualOverride && !myTicketState) {
              updateStatusButtonLabel();
              filterDomTicketRows();
            }
          }, 1200);

          // Initial setup on load
          setTimeout(runStatusSelectionWorkflow, 600);
          setTimeout(updateStatusButtonLabel, 1000);
          setTimeout(filterDomTicketRows, 1000);
        })();
        """;
}
