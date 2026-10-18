(component
  (import "host-log" (func $host_log (param "message" string)))
  (import "host-time" (func $host_time (result u64)))

  (core module $libc
    (memory (export "memory") 1)
    (func (export "realloc") (param i32 i32 i32 i32) (result i32)
      i32.const 4096))
  (core instance $libc_i (instantiate $libc))

  (core module $main
    (import "libc" "memory" (memory 1))
    (import "host" "log" (func $log (param i32 i32)))
    (import "host" "time" (func $time (result i64)))
    (data (i32.const 16) "{\"ok\":true}")
    (data (i32.const 128) "{\"success\":true,\"summary\":\"wasm tool completed\",\"output\":\"wasm-tool-v1\"}")
    (func (export "health") (result i32)
      (call $time)
      drop
      (call $log (i32.const 16) (i32.const 11))
      (i32.store (i32.const 0) (i32.const 16))
      (i32.store (i32.const 4) (i32.const 11))
      (i32.const 0))
    (func (export "execute") (param i32 i32) (result i32)
      (call $time)
      drop
      (call $log (i32.const 128) (i32.const 72))
      (i32.store (i32.const 0) (i32.const 128))
      (i32.store (i32.const 4) (i32.const 72))
      (i32.const 0)))

  (core func $log_lower
    (canon lower (func $host_log) (memory (core memory $libc_i "memory"))))
  (core func $time_lower (canon lower (func $host_time)))
  (core instance $host_i
    (export "log" (func $log_lower))
    (export "time" (func $time_lower)))
  (core instance $main_i
    (instantiate $main
      (with "libc" (instance $libc_i))
      (with "host" (instance $host_i))))

  (func (export "health") (result string)
    (canon lift (core func $main_i "health")
      (memory (core memory $libc_i "memory"))
      (realloc (core func $libc_i "realloc"))))
  (func (export "execute") (param "input" string) (result string)
    (canon lift (core func $main_i "execute")
      (memory (core memory $libc_i "memory"))
      (realloc (core func $libc_i "realloc")))))
