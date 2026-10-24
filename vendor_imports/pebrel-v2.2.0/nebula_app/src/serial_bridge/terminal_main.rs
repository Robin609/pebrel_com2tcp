//! Console-subsystem entry point for a PTY child, never another GUI window.
//! The main GUI executable's serial-connect command reuses the same client.
use clap::Parser;

mod client;

#[derive(Parser)]
#[command(name = "pebrel-serial", about = "Pebrel raw TCP terminal transport")]
struct Options {
    #[command(flatten)]
    connection: client::ClientArgs,
}

fn main() -> std::io::Result<()> {
    client::run(Options::parse().connection)
}
