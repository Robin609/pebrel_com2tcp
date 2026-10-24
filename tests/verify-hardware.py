"""Site acceptance for the ESP32-C3 (2011) and Linux shell (2012).
Only help, printf and ls are transmitted. Confirm a Linux shell is active first.
"""
import argparse
import concurrent.futures
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import select
import socket
import subprocess
import time

ROOT = Path(__file__).resolve().parent

def require(condition, message):
    if not condition:
        raise AssertionError(message)

def connect_group(host, port, count=3):
    sockets = [socket.create_connection((host, port), timeout=3) for _ in range(count)]
    for client in sockets:
        client.setblocking(False)
    time.sleep(0.15)
    return sockets

def collect(clients, duration=1.0, reply_cursor=False):
    received = [bytearray() for _ in clients]
    answered = 0
    end = time.monotonic() + duration
    while time.monotonic() < end:
        ready, _, _ = select.select(clients, [], [], min(0.05, max(0, end - time.monotonic())))
        for client in ready:
            block = client.recv(65536)
            require(bool(block), 'server closed an active client unexpectedly')
            index = clients.index(client)
            received[index].extend(block)
            if reply_cursor and index == 0:
                queries = received[0].count(b'\x1b[6n')
                if queries > answered:
                    clients[0].sendall(b'\x1b[1;1R' * (queries - answered))
                    answered = queries
    return [bytes(value) for value in received]

def printf_command(marker):
    octal = ''.join('\\%03o' % byte for byte in marker.encode('ascii'))
    return ("printf '" + octal + "\\n'\r").encode('ascii')

def check_round(clients, writer, command, marker, label, cursor=False):
    clients[writer].sendall(command)
    outputs = collect(clients, 1.1, cursor)
    require(all(marker in value for value in outputs), label + ': executed response missing')
    require(len(set(outputs)) == 1, label + ': clients received different broadcast bytes')
    return {'case': label, 'writer': writer + 1, 'clients': len(clients),
            'bytes_per_client': len(outputs[0]), 'identical_broadcast': True, 'pass': True}

def run_cli(command, payload, marker, label):
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        process.stdin.write(payload)
        process.stdin.flush()
        time.sleep(1.0)
        process.stdin.close()
        process.stdin = None
        output, error = process.communicate(timeout=5)
        require(process.returncode == 0, label + ': client failed: ' + repr(error))
        require(marker in output, label + ': device output missing: ' + repr(output[-600:]))
        return {'case': label, 'exit_code': process.returncode, 'bytes_received': len(output), 'pass': True,
                'output_sample': output[-900:].decode('utf-8', 'replace')}
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=3)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--host', default='127.0.0.1')
    parser.add_argument('--esp32-port', type=int, default=2011)
    parser.add_argument('--linux-port', type=int, default=2012)
    parser.add_argument('--linux-shell-confirmed', action='store_true')
    args = parser.parse_args()
    require(args.linux_shell_confirmed,
            'Confirm the Linux serial session is at a shell (not nmtui/login/another program), then use --linux-shell-confirmed.')
    results = []
    esp = connect_group(args.host, args.esp32_port)
    linux = connect_group(args.host, args.linux_port)
    try:
        collect(esp, .2, True)
        collect(linux, .2)
        with concurrent.futures.ThreadPoolExecutor(2) as pool:
            for writer in range(3):
                marker = ('C2TCP_HW_%d_OK' % writer).encode('ascii')
                a = pool.submit(check_round, esp, writer, b'help\r', b'Print the summary',
                                'COM11 ESP32 help / writer %d' % (writer + 1), True)
                b = pool.submit(check_round, linux, writer, printf_command(marker.decode()), marker,
                                'COM12 Linux printf / writer %d' % (writer + 1))
                results.extend([a.result(), b.result()])
    finally:
        for client in esp + linux:
            client.close()
    time.sleep(.2)
    with concurrent.futures.ThreadPoolExecutor(2) as pool:
        def reconnect(port, command, marker, name, cursor):
            clients = connect_group(args.host, port, 1)
            try:
                return check_round(clients, 0, command, marker, name, cursor)
            finally:
                clients[0].close()
        a = pool.submit(reconnect, args.esp32_port, b'help\r', b'Print the summary', 'COM11 reconnect', True)
        b = pool.submit(reconnect, args.linux_port, printf_command('C2TCP_RECONNECT_OK'), b'C2TCP_RECONNECT_OK', 'COM12 reconnect', False)
        results.extend([a.result(), b.result()])
    bash = Path(os.environ['APPDATA']) / 'MobaXterm/slash/mx86_64b/bin/bash.exe'
    require(bash.exists(), 'MobaXterm bash not found: ' + str(bash))
    results.append(run_cli([str(bash), '--login', '-c', 'exec nc %s %d' % (args.host, args.linux_port)],
                           b'ls\n', b'lubancat', 'MobaXterm built-in nc / ls'))
    nc = Path(os.environ['USERPROFILE']) / 'scoop/shims/nc.exe'
    command = printf_command('C2TCP_WINDOWS_NC_OK').replace(b'\r', b'\n')
    results.append(run_cli([str(nc), args.host, str(args.linux_port)], command,
                           b'C2TCP_WINDOWS_NC_OK', 'Windows nc launcher / device printf'))
    exe = ROOT.parent / 'bin/Com2Tcp.exe'
    report = {'time': datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=8))).isoformat(),
              'binary': str(exe), 'binary_sha256': hashlib.sha256(exe.read_bytes()).hexdigest(),
              'channels': [{'serial': 'COM11', 'baud': 115200, 'tcp': args.esp32_port},
                           {'serial': 'COM12', 'baud': 1500000, 'tcp': args.linux_port}],
              'simultaneous_tcp_clients': 6, 'cases': results, 'pass': True}
    output = ROOT / 'results/hardware-results.json'
    output.parent.mkdir(exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    for case in results:
        print('PASS', case['case'], 'bytes=', case.get('bytes_per_client', case.get('bytes_received')))
    print('PASS REAL HARDWARE: 2 physical serial ports, 6 simultaneous clients; each client read/write; reconnect; Moba and Windows nc.')
    print('Report:', output)

if __name__ == '__main__':
    main()
