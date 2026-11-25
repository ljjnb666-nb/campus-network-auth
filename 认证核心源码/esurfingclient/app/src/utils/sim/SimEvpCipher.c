#include "SimEvpInternal.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdbool.h>
#include <limits.h>

struct myssl_cipher_ctx_st
{
    const EVP_CIPHER* cipher;
    int enc;
    int padding;
    uint8_t key[24];
    uint8_t iv[16];
    uint8_t chain[16];
    uint8_t buf[16];
    size_t buflen;
    uint8_t hold[16];
    int have_hold;
    uint8_t rk[176];
    uint64_t dk[3][16];
};

/* ---------------------------- 对称密码 ---------------------------- */

EVP_CIPHER_CTX* EVP_CIPHER_CTX_new(void)
{
    EVP_CIPHER_CTX* ctx = (EVP_CIPHER_CTX*)malloc(sizeof(EVP_CIPHER_CTX));
    if (ctx) memset(ctx, 0, sizeof(*ctx));
    return ctx;
}

static void clear_cipher_context(EVP_CIPHER_CTX* ctx)
{
    simssl_secure_zero(ctx, sizeof(*ctx));
}

void EVP_CIPHER_CTX_free(EVP_CIPHER_CTX* ctx)
{
    if (ctx)
    {
        clear_cipher_context(ctx);
        free(ctx);
    }
}

int EVP_CIPHER_CTX_set_padding(EVP_CIPHER_CTX* ctx, int padding)
{
    if (!ctx) return 0;
    ctx->padding = padding ? 1 : 0;
    return 1;
}

static int simssl_cipher_init(EVP_CIPHER_CTX* ctx, const EVP_CIPHER* type,
                             const unsigned char* key, const unsigned char* iv, int enc)
{
    if (!ctx || !type) return 0;

    ctx->cipher = type;
    ctx->enc = enc;
    ctx->buflen = 0;
    ctx->have_hold = 0;
    simssl_secure_zero(ctx->buf, sizeof(ctx->buf));
    simssl_secure_zero(ctx->hold, sizeof(ctx->hold));
    /* 与 OpenSSL 相同：默认开启 padding，需显式 set_padding(ctx, 0) 关闭 */
    ctx->padding = 1;

    if (key)
    {
        simssl_secure_zero(ctx->key, sizeof(ctx->key));
        simssl_secure_zero(ctx->rk, sizeof(ctx->rk));
        simssl_secure_zero(ctx->dk, sizeof(ctx->dk));
        memcpy(ctx->key, key, (size_t)type->key_len);
        if (type->alg == MYSSL_ALG_AES_128_ECB || type->alg == MYSSL_ALG_AES_128_CBC)
        {
            aes128_expand_key(ctx->key, ctx->rk);
        }
        else
        {
            des_key_schedule(ctx->key, ctx->dk[0]);
            des_key_schedule(ctx->key + 8, ctx->dk[1]);
            des_key_schedule(ctx->key + 16, ctx->dk[2]);
        }
    }

    if (type->iv_len > 0 && iv)
    {
        simssl_secure_zero(ctx->iv, sizeof(ctx->iv));
        memcpy(ctx->iv, iv, (size_t)type->iv_len);
    }
    memset(ctx->chain, 0, sizeof(ctx->chain));
    if (type->iv_len > 0) memcpy(ctx->chain, ctx->iv, (size_t)type->iv_len);
    return 1;
}

int EVP_EncryptInit_ex(EVP_CIPHER_CTX* ctx, const EVP_CIPHER* type, void* impl,
                       const unsigned char* key, const unsigned char* iv)
{
    (void)impl;
    return simssl_cipher_init(ctx, type, key, iv, 1);
}

int EVP_DecryptInit_ex(EVP_CIPHER_CTX* ctx, const EVP_CIPHER* type, void* impl,
                       const unsigned char* key, const unsigned char* iv)
{
    (void)impl;
    return simssl_cipher_init(ctx, type, key, iv, 0);
}

/* 单块处理，含 ECB/CBC 模式与链值维护 */
static void simssl_crypt_block(EVP_CIPHER_CTX* ctx, const uint8_t* in, uint8_t* out)
{
    const int bs = ctx->cipher->block_size;
    int is_des = (ctx->cipher->alg == MYSSL_ALG_DES_EDE3_ECB ||
                  ctx->cipher->alg == MYSSL_ALG_DES_EDE3_CBC);
    int is_cbc = (ctx->cipher->alg == MYSSL_ALG_AES_128_CBC ||
                  ctx->cipher->alg == MYSSL_ALG_DES_EDE3_CBC);
    uint8_t tmp[16];
    int i;

    if (is_cbc)
    {
        if (ctx->enc)
        {
            /* CBC 加密：C = E(P xor chain)，链值更新为密文块 */
            for (i = 0; i < bs; i++) tmp[i] = (uint8_t)(in[i] ^ ctx->chain[i]);
            if (is_des) simssl_des3_encrypt_block(ctx->key, tmp, out);
            else aes128_encrypt_block_rk(ctx->rk, tmp, out);
            memcpy(ctx->chain, out, (size_t)bs);
        }
        else
        {
            /* CBC 解密：P = D(C) xor chain，链值更新为密文块 */
            if (is_des) simssl_des3_decrypt_block(ctx->key, in, tmp);
            else aes128_decrypt_block_rk(ctx->rk, in, tmp);
            for (i = 0; i < bs; i++) out[i] = (uint8_t)(tmp[i] ^ ctx->chain[i]);
            memcpy(ctx->chain, in, (size_t)bs);
        }
        simssl_secure_zero(tmp, sizeof(tmp));
        return;
    }

    if (is_des)
    {
        if (ctx->enc) simssl_des3_encrypt_block(ctx->key, in, out);
        else simssl_des3_decrypt_block(ctx->key, in, out);
    }
    else
    {
        if (ctx->enc) aes128_encrypt_block_rk(ctx->rk, in, out);
        else aes128_decrypt_block_rk(ctx->rk, in, out);
    }
}

static int simssl_update(EVP_CIPHER_CTX* ctx, unsigned char* out, int* outl,
                        const unsigned char* in, int inl)
{
    if (!ctx || !ctx->cipher || !outl || inl < 0) return 0;
    *outl = 0;
    const int bs = ctx->cipher->block_size;
    if (inl > 0 && (!in || !out || inl > INT_MAX - bs)) return 0;
    int produced = 0;
    const uint8_t* p = in;
    int left = inl;

    while (left > 0)
    {
        size_t take = (size_t)bs - ctx->buflen;
        if (take > (size_t)left) take = (size_t)left;
        memcpy(ctx->buf + ctx->buflen, p, take);
        ctx->buflen += take;
        p += take;
        left -= (int)take;

        if (ctx->buflen == (size_t)bs)
        {
            if (!ctx->enc && ctx->padding)
            {
                /* 解密且开启 padding：延迟一块，供 Final 去填充 */
                if (ctx->have_hold)
                {
                    memcpy(out + produced, ctx->hold, (size_t)bs);
                    produced += bs;
                }
                simssl_crypt_block(ctx, ctx->buf, ctx->hold);
                ctx->have_hold = 1;
            }
            else
            {
                simssl_crypt_block(ctx, ctx->buf, out + produced);
                produced += bs;
            }
            simssl_secure_zero(ctx->buf, sizeof(ctx->buf));
            ctx->buflen = 0;
        }
    }

    *outl = produced;
    return 1;
}

int EVP_EncryptUpdate(EVP_CIPHER_CTX* ctx, unsigned char* out, int* outl,
                      const unsigned char* in, int inl)
{
    if (!ctx || !ctx->cipher) return 0;
    return simssl_update(ctx, out, outl, in, inl);
}

int EVP_DecryptUpdate(EVP_CIPHER_CTX* ctx, unsigned char* out, int* outl,
                      const unsigned char* in, int inl)
{
    if (!ctx || !ctx->cipher) return 0;
    return simssl_update(ctx, out, outl, in, inl);
}

int EVP_EncryptFinal_ex(EVP_CIPHER_CTX* ctx, unsigned char* out, int* outl)
{
    if (!ctx || !ctx->cipher || !outl) return 0;
    *outl = 0;
    const int bs = ctx->cipher->block_size;
    if (ctx->padding && !out) return 0;

    if (!ctx->padding)
    {
        /* 关闭 padding 时，残留不足一块的数据视为错误（与 OpenSSL 一致） */
        if (ctx->buflen == 0) return 1;
        clear_cipher_context(ctx);
        return 0;
    }

    {
        uint8_t pad = (uint8_t)(bs - (int)ctx->buflen);
        memset(ctx->buf + ctx->buflen, pad, pad);
        ctx->buflen = 0;
        simssl_crypt_block(ctx, ctx->buf, out);
        simssl_secure_zero(ctx->buf, sizeof(ctx->buf));
        *outl = bs;
    }
    return 1;
}

int EVP_DecryptFinal_ex(EVP_CIPHER_CTX* ctx, unsigned char* out, int* outl)
{
    if (!ctx || !ctx->cipher || !outl) return 0;
    *outl = 0;
    const int bs = ctx->cipher->block_size;
    if (ctx->padding && !out) return 0;

    if (!ctx->padding)
    {
        if (ctx->buflen == 0) return 1;
        clear_cipher_context(ctx);
        return 0;
    }

    if (ctx->buflen != 0 || !ctx->have_hold) { clear_cipher_context(ctx); return 0; }

    {
        uint8_t pad = ctx->hold[bs - 1];
        int i;
        if (pad == 0 || pad > bs) { clear_cipher_context(ctx); return 0; }
        for (i = bs - pad; i < bs; i++)
        {
            if (ctx->hold[i] != pad) { clear_cipher_context(ctx); return 0; }
        }
        memcpy(out, ctx->hold, (size_t)(bs - pad));
        *outl = bs - pad;
        simssl_secure_zero(ctx->hold, sizeof(ctx->hold));
        ctx->have_hold = 0;
    }
    return 1;
}

/* Inspect buffers only while owned, never after free. Synthetic test data. */
static bool bytes_are_zero(const void* data, size_t len)
{
    const unsigned char* p = data;
    for (size_t i = 0; i < len; ++i) if (p[i] != 0) return false;
    return true;
}

bool simssl_cipher_cleanup_self_test(void)
{
    const EVP_CIPHER* types[] = {
        EVP_aes_128_ecb(), EVP_aes_128_cbc(),
        EVP_des_ede3_ecb(), EVP_des_ede3_cbc()
    };
    unsigned char key[24], iv[16], input[23], encrypted[64], output[64];
    for (size_t i = 0; i < sizeof(key); ++i) key[i] = (unsigned char)(i + 1);
    memset(iv, 0x5a, sizeof(iv));
    memset(input, 0xa5, sizeof(input));
    EVP_CIPHER_CTX ctx = {0};
    bool ok = true;
    int n = 0, tail = 0, decoded = 0;
    if (EVP_EncryptFinal_ex(NULL, output, &n) != 0 ||
        EVP_DecryptFinal_ex(NULL, output, &n) != 0) return false;
    for (size_t i = 0; i < sizeof(types) / sizeof(types[0]); ++i)
    {
        if (!EVP_EncryptInit_ex(&ctx, types[i], NULL, key, iv) ||
            !EVP_EncryptUpdate(&ctx, encrypted, &n, input, sizeof(input)) ||
            !EVP_EncryptFinal_ex(&ctx, encrypted + n, &tail) ||
            !bytes_are_zero(ctx.buf, sizeof(ctx.buf)) ||
            !EVP_DecryptInit_ex(&ctx, types[i], NULL, key, iv) ||
            !EVP_DecryptUpdate(&ctx, output, &decoded, encrypted, n + tail) ||
            !EVP_DecryptFinal_ex(&ctx, output + decoded, &tail) ||
            decoded + tail != sizeof(input) || memcmp(input, output, sizeof(input)) != 0 ||
            !bytes_are_zero(ctx.buf, sizeof(ctx.buf)) ||
            !bytes_are_zero(ctx.hold, sizeof(ctx.hold)))
        {
            ok = false;
            break;
        }
        clear_cipher_context(&ctx);
        if (!bytes_are_zero(&ctx, sizeof(ctx))) { ok = false; break; }
    }
    /* Failed finalization invalidates the context and wipes all owned bytes. */
    if (EVP_DecryptInit_ex(&ctx, EVP_aes_128_ecb(), NULL, key, iv))
    {
        memset(ctx.hold, 0xa5, sizeof(ctx.hold));
        ctx.hold[15] = 0;
        ctx.have_hold = 1;
        ok = ok && !EVP_DecryptFinal_ex(&ctx, output, &tail) &&
             bytes_are_zero(&ctx, sizeof(ctx));
    }
    else ok = false;
    clear_cipher_context(&ctx);
    simssl_secure_zero(key, sizeof(key));
    simssl_secure_zero(iv, sizeof(iv));
    simssl_secure_zero(input, sizeof(input));
    simssl_secure_zero(encrypted, sizeof(encrypted));
    simssl_secure_zero(output, sizeof(output));
    return ok;
}
