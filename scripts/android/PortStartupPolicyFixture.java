import com.chillasart.theclosingshift.port.PortConfig;

public final class PortStartupPolicyFixture {
    private static int checks;
    private static void check(boolean value) {
        if (!value) throw new AssertionError("Startup policy check " + checks);
        checks++;
    }
    public static void main(String[] args) {
        long limit = 3L * 1024 * 1024 * 1024;
        check(PortConfig.startupArguments(null, 0) == null);
        check(PortConfig.startupArguments(null, -1) == null);
        check(PortConfig.startupArguments(null, limit + 1) == null);
        String original = "-force-gles31";
        check(PortConfig.startupArguments(original, 8L * 1024 * 1024 * 1024) == original);
        String low = PortConfig.startupArguments(null, 2691L * 1048576);
        check(low.contains("-force-gfx-direct") && low.contains("-job-worker-count 2"));
        check(PortConfig.startupArguments(null, limit).equals(low));
        check(PortConfig.startupArguments(original, limit).startsWith(original));
        check(PortConfig.startupArguments(low, limit).equals(low));
        String explicitWorkers = "-job-worker-count 4";
        String result = PortConfig.startupArguments(explicitWorkers, limit);
        check(result.contains(explicitWorkers) && !result.contains("-job-worker-count 2"));
        check(result.contains("-force-gfx-direct"));
        System.out.println("STARTUP_POLICY_PASS assertions=" + checks);
    }
}
