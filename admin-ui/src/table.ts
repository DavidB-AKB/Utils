// A generic, reusable row/column table renderer - not audit-specific despite most callers in
// these templates using it for an audit trail, it's also the base for every list view (users,
// permissions, roles, comments, ...). Deliberately dumb: it doesn't know about pagination,
// filtering, or your data's shape, only how to turn `T[]` + column definitions into a <table>.

export interface TableColumn<T> {
  header: string;
  // Returning a Node (not just a string) lets a column render an action button, a status badge,
  // etc. - render() returning a plain string is the common case and gets wrapped in a text node.
  render(row: T): string | Node;
}

export function renderTable<T>(container: HTMLElement, rows: T[], columns: TableColumn<T>[]): void {
  container.innerHTML = "";
  const table = document.createElement("table");
  table.className = "admin-ui-table";

  const thead = document.createElement("thead");
  const headerRow = document.createElement("tr");
  for (const column of columns) {
    const th = document.createElement("th");
    th.textContent = column.header;
    headerRow.appendChild(th);
  }

  thead.appendChild(headerRow);
  table.appendChild(thead);

  const tbody = document.createElement("tbody");
  if (rows.length === 0) {
    const row = document.createElement("tr");
    const cell = document.createElement("td");
    cell.colSpan = columns.length;
    cell.className = "admin-ui-empty";
    cell.textContent = "Nothing to show.";
    row.appendChild(cell);
    tbody.appendChild(row);
  } else {
    for (const row of rows) {
      const tr = document.createElement("tr");
      for (const column of columns) {
        const td = document.createElement("td");
        const rendered = column.render(row);
        if (typeof rendered === "string") {
          td.textContent = rendered;
        } else {
          td.appendChild(rendered);
        }

        tr.appendChild(td);
      }

      tbody.appendChild(tr);
    }
  }

  table.appendChild(tbody);
  container.appendChild(table);
}
