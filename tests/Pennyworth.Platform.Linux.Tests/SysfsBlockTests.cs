using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux.Tests;

public class SysfsBlockTests
{
    /// <summary>This machine's <c>/proc/self/mountinfo</c>, trimmed to the lines that matter: seven btrfs subvolume mounts that have different device numbers but one source.</summary>
    private const string MountInfo = """
        43 1 0:39 /@/.snapshots/499/snapshot / rw,relatime shared:1 - btrfs /dev/nvme0n1p2 rw,seclabel,ssd,discard=async,space_cache=v2,subvolid=762,subvol=/@/.snapshots/499/snapshot
        39 43 0:7 / /dev rw,nosuid shared:2 - devtmpfs devtmpfs rw,seclabel,size=16256020k,nr_inodes=4064005,mode=755,inode64
        45 43 0:26 / /sys rw,nosuid,nodev,noexec,relatime shared:5 - sysfs sysfs rw,seclabel
        55 43 0:38 /@/.snapshots /.snapshots rw,relatime shared:45 - btrfs /dev/nvme0n1p2 rw,seclabel,ssd,discard=async,space_cache=v2,subvolid=262,subvol=/@/.snapshots
        59 43 0:46 /@/root /root rw,relatime shared:49 - btrfs /dev/nvme0n1p2 rw,seclabel,ssd,discard=async,space_cache=v2,subvolid=260,subvol=/@/root
        58 43 0:47 /@/opt /opt rw,relatime shared:51 - btrfs /dev/nvme0n1p2 rw,seclabel,ssd,discard=async,space_cache=v2,subvolid=261,subvol=/@/opt
        62 43 0:48 /@/srv /srv rw,relatime shared:53 - btrfs /dev/nvme0n1p2 rw,seclabel,ssd,discard=async,space_cache=v2,subvolid=259,subvol=/@/srv
        64 43 0:49 / /tmp rw,nosuid,nodev shared:55 - tmpfs tmpfs rw,seclabel,size=16340776k,nr_inodes=1048576,inode64,usrquota
        66 43 0:50 /@/usr/local /usr/local rw,relatime shared:57 - btrfs /dev/nvme0n1p2 rw,seclabel,ssd,discard=async,space_cache=v2,subvolid=258,subvol=/@/usr/local
        67 43 0:51 /@/var /var rw,relatime shared:59 - btrfs /dev/nvme0n1p2 rw,seclabel,ssd,discard=async,space_cache=v2,subvolid=257,subvol=/@/var
        70 43 259:3 / /boot/efi rw,relatime shared:61 - vfat /dev/nvme0n1p3 rw,fmask=0022,dmask=0077,codepage=437,iocharset=iso8859-1,shortname=mixed,utf8,errors=remount-ro
        72 43 0:52 / /mnt/exram rw,relatime shared:63 - btrfs /dev/sda1 rw,seclabel,ssd,discard=async,space_cache=v2,subvolid=5,subvol=/
        97 43 8:16 / /mnt/wd-blue rw,relatime shared:65 - xfs /dev/sdb rw,seclabel,inode64,logbufs=8,logbsize=32k,noquota
        99 43 8:32 / /mnt/wd-blue-segundo rw,relatime shared:67 - xfs /dev/sdc rw,seclabel,inode64,logbufs=8,logbsize=32k,noquota
        54 43 259:1 / /home rw,relatime shared:47 - ext4 /dev/nvme0n1p1 rw,seclabel,data=ordered
        1076 29 0:113 / /run/user/1000/gvfs rw,nosuid,nodev,relatime shared:1105 - fuse.gvfsd-fuse gvfsd-fuse rw,user_id=1000,group_id=1000
        """;

    private static IEnumerable<string> DiskOfPartition(string source) => source switch
    {
        "/dev/nvme0n1p1" or "/dev/nvme0n1p2" or "/dev/nvme0n1p3" => ["nvme0n1"],
        "/dev/sda1" => ["sda"],
        "/dev/sdb" => ["sdb"],
        "/dev/sdc" => ["sdc"],
        _ => [],
    };

    [Fact]
    public void Seven_subvolume_mounts_of_one_partition_are_one_volume_shown_at_the_shortest_mount_point()
    {
        var byDisk = SysfsBlock.MountPointsByDisk(MountTable.Parse(MountInfo), DiskOfPartition);

        Assert.Equal(["/", "/boot/efi", "/home"], byDisk["nvme0n1"]);
        Assert.Equal(["/mnt/exram"], byDisk["sda"]);
        Assert.Equal(["/mnt/wd-blue"], byDisk["sdb"]);
        Assert.Equal(["/mnt/wd-blue-segundo"], byDisk["sdc"]);
        Assert.Equal(4, byDisk.Count); // tmpfs, devtmpfs, sysfs and fuse mounts belong to no disk.
    }

    [Fact]
    public void A_volume_spanning_two_disks_is_listed_on_both()
    {
        var byDisk = SysfsBlock.MountPointsByDisk(
            MountTable.Parse("1 0 253:0 / /data rw - xfs /dev/mapper/vg-data rw"),
            _ => ["sda", "sdb"]);

        Assert.Equal(["/data"], byDisk["sda"]);
        Assert.Equal(["/data"], byDisk["sdb"]);
    }

    [Fact]
    public void Decodes_udev_escapes_and_the_padding_firmware_adds()
    {
        string encoded = """WDC\x20WD5000AZLX-00K2TA0\x20\x20\x20\x20\x20\x20\x20\x20\x20\x20\x20\x20\x20\x20\x20\x20\x20\x20""";

        Assert.Equal("WDC WD5000AZLX-00K2TA0", SysfsBlock.DecodeUdev(encoded));
        Assert.Equal("Ultra \\x4", SysfsBlock.DecodeUdev("Ultra\\x20\\x4")); // A truncated escape is kept as typed.
        Assert.Equal("Caf\u00e9", SysfsBlock.DecodeUdev("Caf\\xc3\\xa9")); // Multi-byte UTF-8 survives.
    }

    [Fact]
    public void Reads_the_environment_lines_of_a_udev_database_file()
    {
        var udev = SysfsBlock.ParseUdev("""
            S:disk/by-id/ata-WDC_WD5000AZLX-00K2TA0_WD-WCC3F7
            I:7788
            E:ID_BUS=ata
            E:ID_MODEL=WDC_WD5000AZLX-00K2TA0
            E:ID_MODEL_ENC=WDC\x20WD5000AZLX-00K2TA0\x20\x20
            G:systemd
            """);

        Assert.Equal("ata", udev["ID_BUS"]);
        Assert.Equal(@"WDC\x20WD5000AZLX-00K2TA0\x20\x20", udev["ID_MODEL_ENC"]);
        Assert.Equal(3, udev.Count);
    }

    [Theory]
    [InlineData("nvme0n1", "/sys/devices/pci0000:00/0000:00:01.1/0000:02:00.0/nvme/nvme0/nvme0n1", "NVMe")]
    [InlineData("sdb", "/sys/devices/pci0000:00/0000:00:1f.2/ata5/host4/target4:0:0/4:0:0:0/block/sdb", "SATA")]
    [InlineData("sdc", "/sys/devices/pci0000:00/0000:00:14.0/usb2/2-3/2-3:1.0/host6/target6:0:0/6:0:0:0/block/sdc", "USB")]
    [InlineData("mmcblk0", "/sys/devices/pci0000:00/0000:00:1a.0/mmc_host/mmc0/mmc0:0001/block/mmcblk0", "SD")]
    [InlineData("vda", "/sys/devices/pci0000:00/0000:00:05.0/virtio2/block/vda", "Virtual")]
    [InlineData("sda", "/sys/devices/pci0000:00/0000:00:01.0/0000:01:00.0/host0/port-0:0/end_device-0:0/target0:0:0/0:0:0:0/block/sda", "SAS")]
    [InlineData("sdd", "/sys/devices/pci0000:00/0000:00:1f.2/host2/target2:0:0/2:0:0:0/block/sdd", "SCSI")]
    [InlineData("sde", "", "—")]
    public void Names_the_bus_from_where_the_device_hangs_in_the_tree(string name, string path, string bus) =>
        Assert.Equal(bus, SysfsBlock.BusOf(name, path));

    [Fact]
    public void A_path_under_a_folder_called_like_a_bus_does_not_fool_the_bus_check()
    {
        // A test (or a chroot) whose root is /tmp/usb1/ata2/..., ahead of the real /devices/ part.
        Assert.Equal("NVMe", SysfsBlock.BusOf("nvme1n1", "/tmp/usb1/ata2/sys/devices/pci0000:00/0000:00:01.1/nvme/nvme1/nvme1n1"));
        Assert.Equal("—", SysfsBlock.BusOf("sdz", "/tmp/usb1/ata2/sys/devices/platform/something/block/sdz"));
    }

    [Theory]
    [InlineData("loop0", true)]
    [InlineData("zram0", true)]
    [InlineData("dm-3", true)]
    [InlineData("md127", true)]
    [InlineData("sr0", true)]
    [InlineData("ram1", true)]
    [InlineData("nvme0n1", false)]
    [InlineData("sda", false)]
    [InlineData("mmcblk0", false)]
    [InlineData("vda", false)]
    public void Tells_virtual_block_devices_by_name(string name, bool isVirtual) =>
        Assert.Equal(isVirtual, SysfsBlock.IsVirtualName(name));

    [Fact]
    public void Numbered_names_sort_by_value_not_by_text()
    {
        string[] names = ["sdb", "nvme10n1", "nvme2n1", "nvme0n1", "sda"];

        Assert.Equal(["nvme0n1", "nvme2n1", "nvme10n1", "sda", "sdb"], names.OrderBy(SysfsBlock.NaturalKey, StringComparer.Ordinal));
    }

    private static SpecsTree Machine()
    {
        var tree = new SpecsTree();
        // A SATA hard disk with a partition, an NVMe disk, and the virtual devices a real machine also has.
        tree.File("/sys/block/sda/size", "976773168\n").File("/sys/block/sda/queue/rotational", "1\n").File("/sys/block/sda/dev", "8:0\n")
            .File("/sys/block/sda/device/model", "WDC WD5000AZLX-0\n").File("/sys/block/sda/device/vendor", "ATA     \n")
            .File("/sys/block/sda/sda1/partition", "1\n").File("/sys/block/sda/sda1/size", "976771072\n")
            .File("/run/udev/data/b8:0", "E:ID_BUS=ata\nE:ID_MODEL_ENC=WDC\\x20WD5000AZLX-00K2TA0\\x20\\x20\\x20\n")
            .File("/sys/block/nvme0n1/size", "1953525168\n").File("/sys/block/nvme0n1/queue/rotational", "0\n").File("/sys/block/nvme0n1/dev", "259:0\n")
            .File("/sys/block/nvme0n1/device/model", "KINGSTON SNV3S1000G                     \n")
            .File("/sys/block/nvme0n1/nvme0n1p1/partition", "1\n").File("/sys/block/nvme0n1/nvme0n1p2/partition", "2\n")
            .File("/sys/block/loop0/size", "0\n").File("/sys/block/zram0/size", "32680960\n").File("/sys/block/zram0/queue/rotational", "0\n")
            .File("/sys/block/sr0/size", "2097151\n").Dir("/sys/block/sr0/device")
            .File("/sys/block/sdz/size", "0\n").Dir("/sys/block/sdz/device") // An empty card reader slot.
            .File("/sys/block/dm-0/size", "1000000\n").File("/sys/block/dm-0/slaves/nvme0n1p2/partition", "2\n")
            .Link("/sys/block/sdb", "../devices/pci0000:00/0000:00:14.0/usb2/2-3/2-3:1.0/host6/target6:0:0/6:0:0:0/block/sdb")
            .File("/sys/devices/pci0000:00/0000:00:14.0/usb2/2-3/2-3:1.0/host6/target6:0:0/6:0:0:0/block/sdb/size", "62521344\n")
            .File("/sys/devices/pci0000:00/0000:00:14.0/usb2/2-3/2-3:1.0/host6/target6:0:0/6:0:0:0/block/sdb/queue/rotational", "0\n")
            .File("/sys/devices/pci0000:00/0000:00:14.0/usb2/2-3/2-3:1.0/host6/target6:0:0/6:0:0:0/block/sdb/device/model", "Ultra\n")
            .File("/sys/devices/pci0000:00/0000:00:14.0/usb2/2-3/2-3:1.0/host6/target6:0:0/6:0:0:0/block/sdb/device/vendor", "SanDisk \n")
            .File("/sys/devices/pci0000:00/0000:00:14.0/usb2/2-3/2-3:1.0/host6/target6:0:0/6:0:0:0/block/sdb/sdb1/partition", "1\n")
            .Dir("/sys/block/sda/device").Dir("/sys/block/nvme0n1/device");
        // sdb is reached through the symlink: its files must be seen through it, as in sysfs.
        tree.File("/dev/mapper/.keep", "").Link("/dev/mapper/cryptroot", "../dm-0");
        tree.File("/proc/self/mountinfo", """
            1 0 0:39 / / rw - btrfs /dev/mapper/cryptroot rw
            2 1 0:40 /@/var /var rw - btrfs /dev/mapper/cryptroot rw
            3 1 8:1 / /mnt/data rw - xfs /dev/sda1 rw
            4 1 8:17 / /run/media/ana/STICK rw - vfat /dev/sdb1 rw
            5 1 0:41 / /tmp rw - tmpfs tmpfs rw
            6 1 7:0 / /snap/core rw - squashfs /dev/loop0 ro
            """);
        return tree;
    }

    [Fact]
    public void Lists_only_physical_disks_in_natural_order_with_their_details()
    {
        using SpecsTree tree = Machine();

        IReadOnlyList<DiskSpecs> disks = SysfsBlock.Read(new SpecsRoot(tree.Root));

        Assert.Equal(["KINGSTON SNV3S1000G", "WDC WD5000AZLX-00K2TA0", "SanDisk Ultra"], disks.Select(d => d.Model));
        Assert.Equal([0, 1, 2], disks.Select(d => d.Number));
        Assert.Equal([1953525168L * 512, 976773168L * 512, 62521344L * 512], disks.Select(d => d.Size));
        Assert.Equal<bool?[]>([true, false, true], disks.Select(d => d.IsSsd).ToArray());
        Assert.Equal(["NVMe", "—", "USB"], disks.Select(d => d.Bus)); // sda is a plain directory here, not a link into the device tree, so only its name could tell.
    }

    [Fact]
    public void A_mounts_source_is_followed_through_device_mapper_to_the_partition_it_sits_on()
    {
        using SpecsTree tree = Machine();

        IReadOnlyList<DiskSpecs> disks = SysfsBlock.Read(new SpecsRoot(tree.Root));

        Assert.Equal(["/"], disks.Single(d => d.Model == "KINGSTON SNV3S1000G").Letters); // /dev/mapper/cryptroot -> dm-0 -> nvme0n1p2; /var is the same volume.
        Assert.Equal(["/mnt/data"], disks.Single(d => d.Model.StartsWith("WDC", StringComparison.Ordinal)).Letters);
        Assert.Equal(["/run/media/ana/STICK"], disks.Single(d => d.Model == "SanDisk Ultra").Letters);
    }

    [Fact]
    public void Without_sys_block_there_are_no_disks()
    {
        using var tree = new SpecsTree();

        Assert.Empty(SysfsBlock.Read(new SpecsRoot(tree.Root)));
    }

    [Fact]
    public void A_disk_with_no_model_anywhere_is_named_after_its_device()
    {
        using var tree = new SpecsTree();
        tree.File("/sys/block/vda/size", "2097152\n").Dir("/sys/block/vda/device");

        DiskSpecs disk = Assert.Single(SysfsBlock.Read(new SpecsRoot(tree.Root)));

        Assert.Equal("vda", disk.Model);
        Assert.Null(disk.IsSsd);
        Assert.Equal(1024L * 1024 * 1024, disk.Size);
    }
}
