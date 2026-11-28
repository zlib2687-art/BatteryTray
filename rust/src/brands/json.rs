//! Lecteur JSON minimal (lecture seule), pour éviter une dépendance.

#[derive(Debug, PartialEq)]
pub enum Json {
    Null,
    Bool(bool),
    Num(f64),
    Str(String),
    Arr(Vec<Json>),
    Obj(Vec<(String, Json)>),
}

const MAX_DEPTH: usize = 64;

impl Json {
    /// Premier membre d'un objet portant ce nom.
    pub fn get(&self, name: &str) -> Option<&Json> {
        match self {
            Json::Obj(members) => members.iter().find(|m| m.0 == name).map(|m| &m.1),
            _ => None,
        }
    }

    pub fn text(&self, name: &str) -> Option<&str> {
        match self.get(name) {
            Some(Json::Str(s)) => Some(s),
            _ => None,
        }
    }

    pub fn has_duplicate_keys(&self) -> bool {
        match self {
            Json::Obj(m) => m.iter().enumerate().any(|(i, a)| m[..i].iter().any(|b| b.0 == a.0) || a.1.has_duplicate_keys()),
            Json::Arr(items) => items.iter().any(Json::has_duplicate_keys),
            _ => false,
        }
    }
}

pub fn parse(text: &str) -> Option<Json> {
    let mut p = Parser { text, at: 0 };
    let value = p.value(0)?;
    p.space();
    (p.at == text.len()).then_some(value)
}

struct Parser<'a> {
    text: &'a str,
    at: usize,
}

impl Parser<'_> {
    fn peek(&self) -> Option<u8> {
        self.text.as_bytes().get(self.at).copied()
    }

    fn space(&mut self) {
        while matches!(self.peek(), Some(b' ' | b'\t' | b'\r' | b'\n')) {
            self.at += 1;
        }
    }

    fn eat(&mut self, c: u8) -> bool {
        self.space();
        let found = self.peek() == Some(c);
        self.at += usize::from(found);
        found
    }

    fn value(&mut self, depth: usize) -> Option<Json> {
        if depth > MAX_DEPTH {
            return None;
        }
        self.space();
        match self.peek()? {
            b'{' => {
                self.at += 1;
                let mut members = Vec::new();
                if !self.eat(b'}') {
                    loop {
                        self.space();
                        let name = self.string()?;
                        if !self.eat(b':') {
                            return None;
                        }
                        members.push((name, self.value(depth + 1)?));
                        if self.eat(b'}') {
                            break;
                        }
                        if !self.eat(b',') {
                            return None;
                        }
                    }
                }
                Some(Json::Obj(members))
            }
            b'[' => {
                self.at += 1;
                let mut items = Vec::new();
                if !self.eat(b']') {
                    loop {
                        items.push(self.value(depth + 1)?);
                        if self.eat(b']') {
                            break;
                        }
                        if !self.eat(b',') {
                            return None;
                        }
                    }
                }
                Some(Json::Arr(items))
            }
            b'"' => self.string().map(Json::Str),
            _ => {
                let rest = &self.text[self.at..];
                for (word, value) in [("true", Json::Bool(true)), ("false", Json::Bool(false)), ("null", Json::Null)] {
                    if rest.starts_with(word) {
                        self.at += word.len();
                        return Some(value);
                    }
                }
                let len = rest.bytes().take_while(|b| b"+-.eE0123456789".contains(b)).count();
                let number = rest[..len].parse().ok()?;
                self.at += len;
                Some(Json::Num(number))
            }
        }
    }

    fn string(&mut self) -> Option<String> {
        if self.peek()? != b'"' {
            return None;
        }
        self.at += 1;
        let mut out = String::new();
        loop {
            let c = self.text[self.at..].chars().next()?;
            self.at += c.len_utf8();
            match c {
                '"' => return Some(out),
                '\\' => {
                    let e = self.text[self.at..].chars().next()?;
                    self.at += e.len_utf8();
                    out.push(match e {
                        'b' => '\u{8}',
                        'f' => '\u{c}',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        'u' => self.unicode()?,
                        '"' | '\\' | '/' => e,
                        _ => return None,
                    });
                }
                c if c < ' ' => return None,
                c => out.push(c),
            }
        }
    }

    fn hex(&mut self) -> Option<u32> {
        let digits = self.text.get(self.at..self.at + 4)?;
        self.at += 4;
        u32::from_str_radix(digits, 16).ok()
    }

    fn unicode(&mut self) -> Option<char> {
        let first = self.hex()?;
        if (0xD800..0xDC00).contains(&first) && self.text[self.at..].starts_with("\\u") {
            self.at += 2;
            let second = self.hex()?.checked_sub(0xDC00).filter(|&v| v < 0x400)?;
            return char::from_u32(0x10000 + ((first - 0xD800) << 10) + second);
        }
        Some(char::from_u32(first).unwrap_or('\u{FFFD}'))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn values() {
        let v = parse(r#" {"a":[1,true,null],"b":"é😀\n"} "#).unwrap();
        assert_eq!(v.text("b"), Some("é😀\n"));
        assert_eq!(v.get("a"), Some(&Json::Arr(vec![Json::Num(1.0), Json::Bool(true), Json::Null])));
        assert!(!v.has_duplicate_keys());
        assert!(parse(r#"{"a":1,"a":2}"#).unwrap().has_duplicate_keys());
        assert_eq!(parse("{\"a\":1} x"), None);
        assert_eq!(parse(&"[".repeat(1000)), None);
    }
}
