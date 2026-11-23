// One driver's run through DaffyZoom, for scripts/verify-drivers.ps1: Microsoft's JDBC driver.
//
//   javac -cp mssql-jdbc.jar Probe.java && java -cp .;mssql-jdbc.jar Probe
//
// Reads its plan from PROBE_* environment variables and prints one JSON line per step, as
// probe.py does. Besides the common steps it runs a PreparedStatement again on each of three
// connections: the driver sends its first run as sp_executesql (warm1..3), its second as
// sp_prepexec (prepexec1..3), and a third (execute) by the handle that one returned (sp_execute).

import java.security.MessageDigest;
import java.sql.*;
import java.util.HexFormat;

public class Probe {
    static final String URL = System.getenv("PROBE_URL");
    static final String USER = System.getenv("PROBE_USER");
    static final String PASSWORD = System.getenv("PROBE_PASSWORD");
    static final long PAUSE = (long) (Double.parseDouble(env("PROBE_PAUSE", "0.5")) * 1000);

    static String env(String name, String fallback) {
        String value = System.getenv(name);
        return value == null ? fallback : value;
    }

    public static void main(String[] args) throws Exception {
        String batch = System.getenv("PROBE_BATCH");
        String param = System.getenv("PROBE_PARAM").replace("@fromYear", "?");
        String[] values = System.getenv("PROBE_VALUES").split(",");
        int year = Integer.parseInt(values[0]), other = Integer.parseInt(values[1]);
        String state = System.getenv("PROBE_STATE");

        fresh("who", System.getenv("PROBE_WHO"), null);
        for (int i = 1; i <= 2; i++) { fresh("batch" + i, batch, null); Thread.sleep(PAUSE); }

        try (Connection c = DriverManager.getConnection(URL, USER, PASSWORD)) {
            statement("stateBefore", c, state);
            statement("batch3", c, batch);
            statement("stateAfter", c, state);
        } catch (SQLException ex) {
            emitError("stateBefore", "connect: " + ex);
        }

        for (int i = 1; i <= 3; i++) { fresh("param" + i, param, year); Thread.sleep(PAUSE); }
        fresh("paramOther", param, other);

        // One PreparedStatement per connection, run again: warm (sp_executesql, the request param1..3
        // made), then prepexec (sp_prepexec). On the third connection prepexec is served from the
        // cache, and execute runs by the handle that hit carried: it must be this session's own.
        for (int i = 1; i <= 3; i++) {
            try (Connection c = DriverManager.getConnection(URL, USER, PASSWORD);
                 PreparedStatement p = c.prepareStatement(param)) {
                prepared("warm" + i, p, year);
                prepared("prepexec" + i, p, year);
                if (i == 3) prepared("execute", p, year);
            } catch (SQLException ex) {
                emitError("prepexec" + i, "connect: " + ex);
            }
            Thread.sleep(PAUSE);
        }
    }

    static void prepared(String step, PreparedStatement p, int value) {
        try {
            p.setInt(1, value);
            long start = System.nanoTime();
            read(step, p.execute(), p, start);
        } catch (Exception ex) {
            emitError(step, ex.toString());
        }
    }

    static void fresh(String step, String sql, Integer value) {
        try (Connection c = DriverManager.getConnection(URL, USER, PASSWORD)) {
            if (value == null) statement(step, c, sql);
            else try (PreparedStatement p = c.prepareStatement(sql)) {
                p.setInt(1, value);
                long start = System.nanoTime();
                read(step, p.execute(), p, start);
            }
        } catch (Exception ex) {
            emitError(step, ex.toString());
        }
    }

    static void statement(String step, Connection c, String sql) {
        try (Statement s = c.createStatement()) {
            long start = System.nanoTime();
            read(step, s.execute(sql), s, start);
        } catch (Exception ex) {
            emitError(step, ex.toString());
        }
    }

    /** Reads every row of every result set, then reports the step. */
    static void read(String step, boolean isResult, Statement s, long start) throws Exception {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        long rows = 0;
        String first = null;
        while (true) {
            if (isResult) {
                try (ResultSet r = s.getResultSet()) {
                    ResultSetMetaData m = r.getMetaData();
                    StringBuilder names = new StringBuilder();
                    for (int i = 1; i <= m.getColumnCount(); i++) names.append(m.getColumnName(i)).append(',');
                    digest.update(names.toString().getBytes("UTF-8"));
                    while (r.next()) {
                        StringBuilder row = new StringBuilder();
                        for (int i = 1; i <= m.getColumnCount(); i++) {
                            if (i > 1) row.append('|');
                            Object v = r.getObject(i);
                            row.append(v == null ? "" : v.toString());
                        }
                        digest.update(row.toString().getBytes("UTF-8"));
                        digest.update((byte) '\n');
                        if (first == null) first = row.toString();
                        rows++;
                    }
                }
            } else if (s.getUpdateCount() == -1) {
                break;
            }
            isResult = s.getMoreResults();
        }
        double seconds = (System.nanoTime() - start) / 1e9;
        System.out.println("{\"step\":" + quote(step) + ",\"seconds\":" + seconds + ",\"rows\":" + rows +
            ",\"digest\":" + quote(HexFormat.of().formatHex(digest.digest())) + ",\"text\":" + quote(first) + "}");
        System.out.flush();
    }

    static void emitError(String step, String error) {
        System.out.println("{\"step\":" + quote(step) + ",\"error\":" + quote(error) + "}");
        System.out.flush();
    }

    static String quote(String text) {
        if (text == null) return "null";
        StringBuilder b = new StringBuilder("\"");
        for (char ch : text.toCharArray()) {
            switch (ch) {
                case '"' -> b.append("\\\"");
                case '\\' -> b.append("\\\\");
                case '\n' -> b.append("\\n");
                case '\r' -> b.append("\\r");
                case '\t' -> b.append("\\t");
                default -> { if (ch < 0x20) b.append(String.format("\\u%04x", (int) ch)); else b.append(ch); }
            }
        }
        return b.append('"').toString();
    }
}
