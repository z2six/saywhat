"""Stdlib-only argument validation: no HTTP/inference imports or server startup."""
import argparse
import unittest

import server


class PortArgumentTests(unittest.TestCase):
    def test_accepts_safe_selected_ports_without_starting_server(self):
        for port in (1024, 25347, 65535):
            self.assertEqual(server.managed_port(str(port)), port)

    def test_rejects_unsafe_or_noncanonical_port_arguments_before_importing_inference(self):
        for value in ("", "0", "-1", "1023", "65536", "+25347", "25347.0", " 25347", "25347 ",
                      "025347", "00000", "２５３４７", "25347;echo", "9" * 100):
            with self.subTest(value=value), self.assertRaises(argparse.ArgumentTypeError):
                server.managed_port(value)


if __name__ == "__main__":
    unittest.main()
